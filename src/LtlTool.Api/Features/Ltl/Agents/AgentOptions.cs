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
    public BillingReadySweeperOptions BillingReadySweeper { get; set; } = new();
    public YardOpportunitySweeperOptions YardOpportunitySweeper { get; set; } = new();
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

/// <summary>
/// BillingReadySweeper: periodically scans the billing worklist and notifies once per load the
/// first time it clears every billing-readiness gate (the <c>BillingReady</c> / T6 stage). Closes
/// the automation gap between "a load became billable" and someone on the billing team noticing —
/// today that state is only visible by opening the Billing tab or waiting for the once-daily AR
/// digest aggregate.
/// </summary>
public sealed class BillingReadySweeperOptions
{
    public bool Enabled { get; set; }

    /// <summary>Sweep cadence. Default 180s (3 min). Floored at 30s so a mis-set config cannot spin the loop.</summary>
    public int IntervalSeconds { get; set; } = 180;
}

/// <summary>
/// YardOpportunitySweeper: periodically scans yard-originated LTL consolidation drafts (received via
/// the Yard webhook boundary, issue #166) and notifies once per draft. Closes the gap between a dock
/// worker/Yard system creating a draft and a dispatcher/load planner noticing it — today that draft
/// only surfaces by opening the Dock screen's incoming-opportunity list. Peer-system data only: never
/// touches Alvys, matching the Yard boundary rules in docs/BOUNDARIES.md.
/// </summary>
public sealed class YardOpportunitySweeperOptions
{
    public bool Enabled { get; set; }

    /// <summary>Sweep cadence. Default 60s (1 min) — dock-floor events are time-sensitive. Floored at 30s.</summary>
    public int IntervalSeconds { get; set; } = 60;
}
