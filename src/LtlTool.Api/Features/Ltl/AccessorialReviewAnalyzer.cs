using System.Globalization;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Deterministic accessorial-review analyzer (Phase 3.5). Sibling of <see cref="EquipmentEventAnalyzer"/>:
/// pure and synchronous — the notes/documents/stops fetch lives in <see cref="LtlLoadService"/>; this
/// only interprets what was fetched and never fabricates a candidate or a dollar value.
///
/// <para>
/// It combines two evidence sources into one typed candidate list, each candidate citing the exact
/// Alvys record it came from:
/// </para>
/// <list type="bullet">
///   <item>Stop timing (<see cref="AlvysTripStop"/>): detention (dwell &gt; per-customer free time),
///     layover (dwell &gt; <see cref="AccessorialReviewOptions.LayoverThresholdHours"/>), weekend /
///     after-hours scheduling, and reconsignment (a stop reference flagged redelivery/reconsign).</item>
///   <item>Note/document keywords already extracted by <see cref="AccessorialSignalAnalyzer"/>, folded
///     in verbatim with their source id and quote.</item>
/// </list>
///
/// <para>
/// Guardrails: detention needs a per-customer free-time threshold from config — when it is not set the
/// candidate is <see cref="AccessorialCandidateStatus.CannotEvaluate"/> ("customer free time not
/// configured"), never an assumed number. When neither stops nor note/document evidence were available
/// the result is <see cref="AccessorialReviewResult.NotEvaluated"/> — an empty candidate list is never
/// a clean bill of health.
/// </para>
/// </summary>
public sealed class AccessorialReviewAnalyzer(IOptions<LtlOptions> options)
{
    private readonly AccessorialReviewOptions _options = options.Value.AccessorialReview;

    // Stop reference name/value tokens (case-insensitive substring) that flag a reconsignment.
    private static readonly string[] ReconsignMarkers =
        ["reconsign", "redeliver", "re-deliver", "redelivery", "reroute", "diverted", "diversion"];

    /// <summary>
    /// Build the accessorial-review candidate list for a load from its trip stops and the already-built
    /// note/document keyword context. Returns <see cref="AccessorialReviewResult.NotEvaluated"/> when
    /// there is nothing to inspect.
    /// </summary>
    public AccessorialReviewResult Analyze(
        AlvysLoad load,
        IReadOnlyList<AlvysTripStop> stops,
        AccessorialReviewContext keywordContext)
    {
        var haveStops = stops.Count > 0;
        if (!haveStops && !keywordContext.Evaluated)
            return AccessorialReviewResult.NotEvaluated;

        var candidates = new List<AccessorialReviewCandidate>();
        var freeTimeMinutes = ResolveCustomerFreeTime(load);
        var detentionUnevaluated = false;

        foreach (var stop in stops)
        {
            var arrived = stop.ArrivedAt ?? stop.ArrivedDate;
            var departed = stop.DepartedAt ?? stop.DepartedDate;

            // Detention / layover need a measurable closed dwell (arrival AND departure).
            if (arrived is not null && departed is not null
                && arrived.Value.Year < 9999 && departed.Value > arrived.Value)
            {
                var dwell = departed.Value - arrived.Value;

                if (dwell.TotalHours >= Math.Max(1, _options.LayoverThresholdHours))
                {
                    candidates.Add(new AccessorialReviewCandidate
                    {
                        Type = AccessorialSignalType.Layover,
                        Status = AccessorialCandidateStatus.Likely,
                        Reason = $"Layover — {dwell.TotalHours:0.#}h dwell at {StopLabel(stop)}",
                        Evidence = StopTimingEvidence(stop, arrived.Value, departed.Value),
                        SourceId = stop.Id,
                        SourceType = "Stop",
                    });
                }
                else if (freeTimeMinutes is { } freeTime)
                {
                    var overMinutes = dwell.TotalMinutes - freeTime;
                    if (overMinutes > 0)
                    {
                        candidates.Add(new AccessorialReviewCandidate
                        {
                            Type = AccessorialSignalType.Detention,
                            Status = AccessorialCandidateStatus.Likely,
                            Reason = $"Detention — {TimeSpan.FromMinutes(overMinutes).TotalHours:0.#}h over free time at {StopLabel(stop)}",
                            Evidence =
                                StopTimingEvidence(stop, arrived.Value, departed.Value)
                                + $", customer free time = {freeTime}m",
                            SourceId = stop.Id,
                            SourceType = "Stop",
                        });
                    }
                }
                else
                {
                    // A closed dwell exists but we cannot judge detention without the customer's
                    // free-time term — surface that honestly (once per load), never assume a number.
                    detentionUnevaluated = true;
                }
            }

            // Reconsignment — a stop reference flagged as a redelivery/reroute.
            var reconsignRef = FindReconsignReference(stop);
            if (reconsignRef is not null)
            {
                candidates.Add(new AccessorialReviewCandidate
                {
                    Type = AccessorialSignalType.Reconsignment,
                    Status = AccessorialCandidateStatus.Likely,
                    Reason = $"Reconsignment — stop reference indicates redelivery/reroute at {StopLabel(stop)}",
                    Evidence = $"stop {StopRef(stop)} reference '{reconsignRef.Name}' = '{reconsignRef.Value}'",
                    SourceId = stop.Id,
                    SourceType = "Stop",
                });
            }

            // Weekend / after-hours scheduling — derived from the stop's scheduled appointment/window.
            var scheduled = StopSchedule(stop);
            if (scheduled is { } when
                && (when.DayOfWeek == DayOfWeek.Saturday || when.DayOfWeek == DayOfWeek.Sunday))
            {
                candidates.Add(new AccessorialReviewCandidate
                {
                    Type = AccessorialSignalType.WeekendDelivery,
                    Status = AccessorialCandidateStatus.Likely,
                    Reason = $"Weekend / after-hours — {StopKind(stop)} scheduled {when.DayOfWeek}",
                    Evidence = $"stop {StopRef(stop)} scheduled {FormatLocal(when)}",
                    SourceId = stop.Id,
                    SourceType = "Stop",
                });
            }
        }

        if (detentionUnevaluated && !candidates.Any(c => c.Type == AccessorialSignalType.Detention))
        {
            candidates.Add(new AccessorialReviewCandidate
            {
                Type = AccessorialSignalType.Detention,
                Status = AccessorialCandidateStatus.CannotEvaluate,
                Reason = "Can't evaluate detention — customer free time not configured",
                Evidence = "A closed stop dwell exists but no free-time term is configured for this customer.",
                SourceType = "Stop",
            });
        }

        // Fold in the note/document keyword signals, each keeping its cited source + quote.
        foreach (var signal in keywordContext.Signals)
        {
            candidates.Add(new AccessorialReviewCandidate
            {
                Type = signal.Type,
                Status = signal.Confidence >= 1.0 && signal.Type != AccessorialSignalType.Other
                    ? AccessorialCandidateStatus.Likely
                    : AccessorialCandidateStatus.Unknown,
                Reason = KeywordReason(signal.Type),
                Evidence = $"{signal.SourceType.ToLowerInvariant()} {signal.SourceId}: \"{signal.EvidenceQuote}\"",
                SourceId = signal.SourceId,
                SourceType = signal.SourceType,
            });
        }

        return new AccessorialReviewResult { Evaluated = true, Candidates = candidates };
    }

    /// <summary>Per-customer free time (minutes) from config, keyed by customer id then name; null when unset.</summary>
    private int? ResolveCustomerFreeTime(AlvysLoad load)
    {
        if (!string.IsNullOrWhiteSpace(load.CustomerId)
            && _options.CustomerFreeTimeMinutes.TryGetValue(load.CustomerId, out var byId))
            return byId;
        if (!string.IsNullOrWhiteSpace(load.CustomerName)
            && _options.CustomerFreeTimeMinutes.TryGetValue(load.CustomerName, out var byName))
            return byName;
        return null;
    }

    private static AlvysReference? FindReconsignReference(AlvysTripStop stop) =>
        stop.References?.FirstOrDefault(r =>
            ReconsignMarkers.Any(m =>
                (r.Name?.Contains(m, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Value?.Contains(m, StringComparison.OrdinalIgnoreCase) ?? false)));

    private static DateTimeOffset? StopSchedule(AlvysTripStop stop) =>
        stop.AppointmentDate ?? stop.Appointment ?? stop.StopWindowStart ?? stop.StopWindow?.Begin;

    private static string KeywordReason(AccessorialSignalType type) => type switch
    {
        AccessorialSignalType.Detention => "Detention review (note/document mention)",
        AccessorialSignalType.Layover => "Layover review (note/document mention)",
        AccessorialSignalType.Lumper => "Lumper / handling review",
        AccessorialSignalType.Reconsignment => "Reconsignment review",
        AccessorialSignalType.Handling => "Handling / lumper review",
        AccessorialSignalType.InsideDelivery => "Inside-delivery review",
        AccessorialSignalType.WeekendDelivery => "Weekend / after-hours review",
        _ => "Possible accessorial — review note",
    };

    private static string StopKind(AlvysTripStop stop) =>
        string.IsNullOrWhiteSpace(stop.StopType) ? "Stop" : stop.StopType!;

    private static string StopLabel(AlvysTripStop stop)
    {
        var where = string.Join(", ", new[] { stop.Address?.City, stop.Address?.State }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var kind = StopKind(stop);
        return where.Length > 0 ? $"{kind} in {where}" : kind;
    }

    private static string StopRef(AlvysTripStop stop) =>
        stop.Id ?? (stop.Sequence is { } seq ? $"seq{seq.ToString(CultureInfo.InvariantCulture)}" : "?");

    private static string StopTimingEvidence(AlvysTripStop stop, DateTimeOffset arrived, DateTimeOffset departed) =>
        $"stop {StopRef(stop)}, arrived {FormatLocal(arrived)}, departed {FormatLocal(departed)}";

    private static string FormatLocal(DateTimeOffset value) =>
        value.ToString("MMM d, yyyy HH:mm 'UTC'zzz", CultureInfo.InvariantCulture);
}
