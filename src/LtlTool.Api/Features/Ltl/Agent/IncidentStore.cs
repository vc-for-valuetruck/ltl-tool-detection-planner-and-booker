using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// In-memory ledger of corridor incidents and the deterministic surge/risk heuristic they drive,
/// ported from the LogisticsRoute incident ledger (<c>report_incident</c> / <c>forecast_route</c>).
///
/// <para>
/// <b>Read-only vs Alvys.</b> Incidents are operator-reported planning signals recorded only in this
/// process — they are never written to Alvys and never read from Alvys. They feed the reference quote
/// estimator's surge factor and a corridor risk snapshot; they touch nothing operational. The
/// LogisticsRoute original mixed a non-deterministic <c>time.time() % 8</c> term into the forecast;
/// that noise is deliberately dropped here so the risk output is reproducible and testable.
/// </para>
/// </summary>
public sealed class IncidentStore(TimeProvider clock, Microsoft.Extensions.Options.IOptions<IncidentRiskOptions> options)
{
    private readonly TimeProvider _clock = clock;
    private readonly IncidentRiskOptions _opts = options.Value;
    private readonly ConcurrentDictionary<string, List<IncidentRecord>> _byCorridor = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Canonical corridor key from two location tokens, e.g. ("TX","TX") → "TX-&gt;TX".</summary>
    public static string CorridorKey(string origin, string destination) =>
        $"{(origin ?? "").Trim().ToUpperInvariant()}->{(destination ?? "").Trim().ToUpperInvariant()}";

    /// <summary>
    /// Record an incident on a corridor and return the updated risk snapshot. Severity is clamped to
    /// 1..5. The note is optional operator context.
    /// </summary>
    public CorridorRisk Report(string origin, string destination, int severity, string? note, string reportedBy)
    {
        var key = CorridorKey(origin, destination);
        var record = new IncidentRecord
        {
            Severity = Math.Clamp(severity, 1, 5),
            Note = note,
            ReportedBy = reportedBy,
            ReportedAt = _clock.GetUtcNow(),
        };
        lock (_gate)
        {
            var list = _byCorridor.GetOrAdd(key, _ => []);
            list.Add(record);
        }
        return GetRisk(origin, destination);
    }

    /// <summary>
    /// Current risk snapshot for a corridor: accumulated severity → surge multiplier, expected delay,
    /// and a Low/Medium/High bucket. Empty corridors return the baseline (surge 1.0, no delay, Low).
    /// </summary>
    public CorridorRisk GetRisk(string origin, string destination)
    {
        var key = CorridorKey(origin, destination);
        List<IncidentRecord> snapshot;
        lock (_gate)
        {
            snapshot = _byCorridor.TryGetValue(key, out var list) ? [.. list] : [];
        }

        var incidentCount = snapshot.Count;
        var severityScore = snapshot.Sum(i => i.Severity);

        var surge = 1.0m + Math.Min(_opts.MaxSurgeBonus, severityScore * _opts.SurgePerSeverityPoint);
        var delayHours = severityScore * _opts.DelayHoursPerSeverityPoint;

        var level = (surge, delayHours) switch
        {
            _ when surge >= _opts.HighSurgeThreshold || delayHours >= _opts.HighDelayHours => IncidentRiskLevel.High,
            _ when surge >= _opts.MediumSurgeThreshold || delayHours >= _opts.MediumDelayHours => IncidentRiskLevel.Medium,
            _ => IncidentRiskLevel.Low,
        };

        return new CorridorRisk
        {
            CorridorKey = key,
            IncidentCount = incidentCount,
            SeverityScore = severityScore,
            SurgeMultiplier = decimal.Round(surge, 3),
            ExpectedDelayHours = decimal.Round(delayHours, 2),
            Level = level,
            LatestNote = snapshot.LastOrDefault()?.Note,
        };
    }

    private sealed class IncidentRecord
    {
        public required int Severity { get; init; }
        public string? Note { get; init; }
        public required string ReportedBy { get; init; }
        public required DateTimeOffset ReportedAt { get; init; }
    }
}

/// <summary>Tuning for the incident → surge/risk heuristic (bound from <c>Ltl:Optimization:Incident</c>).</summary>
public sealed class IncidentRiskOptions
{
    public const string SectionName = "Ltl:Optimization:Incident";

    /// <summary>Surge added per accumulated severity point.</summary>
    public decimal SurgePerSeverityPoint { get; set; } = 0.05m;

    /// <summary>Ceiling on the surge bonus so a flood of incidents can't unbound the multiplier.</summary>
    public decimal MaxSurgeBonus { get; set; } = 1.0m;

    /// <summary>Expected delay hours added per accumulated severity point.</summary>
    public decimal DelayHoursPerSeverityPoint { get; set; } = 0.75m;

    public decimal MediumSurgeThreshold { get; set; } = 1.15m;
    public decimal HighSurgeThreshold { get; set; } = 1.35m;
    public decimal MediumDelayHours { get; set; } = 4m;
    public decimal HighDelayHours { get; set; } = 10m;
}

/// <summary>Coarse corridor risk bucket.</summary>
public enum IncidentRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Risk snapshot for a corridor derived from its accumulated incidents.</summary>
public sealed class CorridorRisk
{
    public required string CorridorKey { get; init; }
    public required int IncidentCount { get; init; }
    public required int SeverityScore { get; init; }
    public required decimal SurgeMultiplier { get; init; }
    public required decimal ExpectedDelayHours { get; init; }
    public required IncidentRiskLevel Level { get; init; }
    public string? LatestNote { get; init; }
}
