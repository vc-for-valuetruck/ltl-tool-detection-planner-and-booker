namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Configuration for the read-only background agents. Bound from <c>Ltl:Agents</c>. Every agent is
/// OFF by default — a fresh clone, CI, and the demo run no sweeps until an operator opts in
/// server-side. No agent ever writes to Alvys.
/// </summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Ltl:Agents";

    public OpportunitySweeperOptions OpportunitySweeper { get; set; } = new();
    public ExceptionSweeperOptions ExceptionSweeper { get; set; } = new();
    public ArDigestOptions ArDigest { get; set; } = new();
}

/// <summary>OpportunitySweeper: periodically scans consolidation opportunities and alerts on high-uplift ones.</summary>
public sealed class OpportunitySweeperOptions
{
    public bool Enabled { get; set; }

    /// <summary>Sweep cadence. Default 300s (5 min). Floored at 30s so a mis-set config cannot spin the loop.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Only opportunities whose projected uplift is at least this many USD raise a notification. Default 500.</summary>
    public decimal UpliftAlertThresholdUsd { get; set; } = 500m;
}

/// <summary>ExceptionSweeper: periodically scans the exception worklist and notifies once per load.</summary>
public sealed class ExceptionSweeperOptions
{
    public bool Enabled { get; set; }

    /// <summary>Sweep cadence. Default 120s (2 min). Floored at 30s.</summary>
    public int IntervalSeconds { get; set; } = 120;
}

/// <summary>ArDigest: once-daily AR / billing-attention digest, in-app only (never an external send).</summary>
public sealed class ArDigestOptions
{
    public bool Enabled { get; set; }

    /// <summary>Local hour of day (0–23) at/after which the day's digest fires. Default 8 (08:00 local).</summary>
    public int HourLocal { get; set; } = 8;
}
