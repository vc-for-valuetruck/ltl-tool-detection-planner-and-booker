using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Background agent (default every 300s) that sweeps the read-only consolidation-opportunity scan
/// and raises an in-app notification for each opportunity whose projected uplift crosses the
/// configured USD threshold. Dedupe is one notification per (parent load, pickup date), so an
/// opportunity that keeps surfacing across polls fires exactly once when it first crosses the
/// threshold.
///
/// <para>
/// Alvys posture: read-only. Reuses <see cref="ConsolidationOpportunityService"/> (which itself only
/// reads Alvys load-search) and the existing <see cref="NotificationDispatcher"/>. Nothing is
/// written to Alvys. If Alvys is unavailable the agent records a 'degraded' heartbeat and emits no
/// notification (see the null-probe below).
/// </para>
/// </summary>
public sealed class OpportunitySweeperService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<AgentsOptions> options,
    ILogger<OpportunitySweeperService> logger)
    : AgentBackgroundService(services, clock, logger)
{
    public const string Name = "opportunity-sweeper";

    private readonly OpportunitySweeperOptions _options = options.Value.OpportunitySweeper;

    public override string AgentName => Name;
    public override bool Enabled => _options.Enabled;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.IntervalSeconds);

    private const int ScanLimit = 50;
    private const int LookbackDays = 14;

    protected override async Task<AgentSweepResult> SweepAsync(IServiceProvider scope, CancellationToken ct)
    {
        // Cheap availability probe against the raw Alvys read. In production the client coalesces a
        // transport failure to an EMPTY response (never null), so a real soft outage surfaces as
        // healthy-with-zero-swept — the honest limitation of the read layer. A null here only occurs
        // on a contract violation, which we surface as degraded rather than pretend is "no work".
        var alvys = scope.GetRequiredService<IAlvysClient>();
        var probe = await alvys.SearchLoadsAsync(page: 1, pageSize: 1, ct: ct);
        if (probe is null)
        {
            return AgentSweepResult.Degraded("AlvysNullResponse");
        }

        var opportunities = scope.GetRequiredService<ConsolidationOpportunityService>();
        var dispatcher = scope.GetRequiredService<NotificationDispatcher>();

        var response = await opportunities.FindOpportunitiesAsync(ScanLimit, LookbackDays, ct);

        var threshold = _options.UpliftAlertThresholdUsd;
        foreach (var opportunity in response.Opportunities)
        {
            ct.ThrowIfCancellationRequested();
            if (opportunity.ProjectedUplift < threshold)
            {
                continue;
            }

            await dispatcher.DispatchAsync(ToTrigger(opportunity), ct);
        }

        return AgentSweepResult.Healthy(response.Opportunities.Count);
    }

    /// <summary>
    /// Maps a high-uplift opportunity to an <see cref="NotificationStage.OpportunityDetected"/>
    /// trigger. <c>SourceKey</c> is the parent load number and <c>OccurredAt</c> is the pickup date
    /// (midnight UTC), so the dispatcher idempotency key is one-per-(parent load, pickup date) — the
    /// same opportunity re-seen across polls fires exactly once. Exposed for unit testing.
    /// </summary>
    public static NotificationTrigger ToTrigger(ConsolidationOpportunity opportunity)
    {
        var parent = opportunity.Parent;
        var lane = $"{opportunity.OriginState}→{opportunity.DestinationState}";
        var customer = string.IsNullOrWhiteSpace(opportunity.CustomerName) ? null : opportunity.CustomerName;
        var siblingCount = opportunity.Siblings.Count;

        var summary = customer is null
            ? $"Consolidation opportunity on {lane} for load {parent.LoadNumber}: "
              + $"{siblingCount} sibling load(s), projected uplift ${opportunity.ProjectedUplift:0}."
            : $"Consolidation opportunity for {customer} on {lane} (load {parent.LoadNumber}): "
              + $"{siblingCount} sibling load(s), projected uplift ${opportunity.ProjectedUplift:0}.";

        return new NotificationTrigger
        {
            Stage = NotificationStage.OpportunityDetected,
            SourceKey = parent.LoadNumber,
            Title = $"Consolidation opportunity · {parent.LoadNumber}",
            Summary = summary,
            LoadId = parent.LoadId,
            LoadNumber = parent.LoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(parent.LoadNumber)}",
            OccurredAt = new DateTimeOffset(opportunity.PickupDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
    }
}
