using LtlTool.Api.Features.Ltl.Agent;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

/// <summary>
/// Tests for the deterministic incident → surge/risk heuristic. The LogisticsRoute original mixed a
/// non-deterministic time term into its forecast; these tests lock in that our port is reproducible.
/// </summary>
public sealed class IncidentStoreTests
{
    private static IncidentStore Build(IncidentRiskOptions? opts = null) =>
        new(new FixedTimeProvider(LtlTestFactory.Now), Microsoft.Extensions.Options.Options.Create(opts ?? new IncidentRiskOptions()));

    [Fact]
    public void Empty_corridor_is_baseline_low_risk_with_no_surge()
    {
        var risk = Build().GetRisk("TX", "IL");
        Assert.Equal(0, risk.IncidentCount);
        Assert.Equal(0, risk.SeverityScore);
        Assert.Equal(1.000m, risk.SurgeMultiplier);
        Assert.Equal(0.00m, risk.ExpectedDelayHours);
        Assert.Equal(IncidentRiskLevel.Low, risk.Level);
    }

    [Fact]
    public void Reporting_an_incident_raises_surge_and_delay_deterministically()
    {
        var store = Build();
        // severity 3 → surge = 1.0 + min(1.0, 3*0.05)=1.15; delay = 3*0.75 = 2.25.
        var risk = store.Report("TX", "IL", severity: 3, note: "ice storm", reportedBy: "tester");
        Assert.Equal(1, risk.IncidentCount);
        Assert.Equal(3, risk.SeverityScore);
        Assert.Equal(1.150m, risk.SurgeMultiplier);
        Assert.Equal(2.25m, risk.ExpectedDelayHours);
        Assert.Equal("ice storm", risk.LatestNote);
    }

    [Fact]
    public void Repeated_report_and_getrisk_are_identical_no_time_noise()
    {
        var store = Build();
        store.Report("TX", "IL", 2, null, "t");
        var a = store.GetRisk("TX", "IL");
        var b = store.GetRisk("TX", "IL");
        Assert.Equal(a.SurgeMultiplier, b.SurgeMultiplier);
        Assert.Equal(a.ExpectedDelayHours, b.ExpectedDelayHours);
        Assert.Equal(a.Level, b.Level);
    }

    [Fact]
    public void Accumulated_severity_escalates_the_risk_level()
    {
        var store = Build();
        store.Report("TX", "IL", 5, null, "t");
        store.Report("TX", "IL", 5, null, "t");
        var risk = store.GetRisk("TX", "IL");
        // severityScore=10 → surge = 1.0 + min(1.0, 10*0.05=0.5) = 1.5 (>=1.35) → High.
        Assert.Equal(IncidentRiskLevel.High, risk.Level);
        Assert.Equal(1.500m, risk.SurgeMultiplier);
    }

    [Fact]
    public void Severity_is_clamped_to_one_through_five()
    {
        var store = Build();
        var low = store.Report("A", "B", severity: -4, note: null, reportedBy: "t");
        Assert.Equal(1, low.SeverityScore); // clamped up to 1

        var store2 = Build();
        var high = store2.Report("A", "B", severity: 99, note: null, reportedBy: "t");
        Assert.Equal(5, high.SeverityScore); // clamped down to 5
    }

    [Fact]
    public void Corridor_key_is_direction_specific_and_case_insensitive()
    {
        Assert.Equal("TX->IL", IncidentStore.CorridorKey("tx", "il"));
        Assert.NotEqual(IncidentStore.CorridorKey("TX", "IL"), IncidentStore.CorridorKey("IL", "TX"));
    }

    [Fact]
    public void Incidents_on_one_corridor_do_not_leak_to_another()
    {
        var store = Build();
        store.Report("TX", "IL", 5, null, "t");
        var other = store.GetRisk("CA", "NY");
        Assert.Equal(IncidentRiskLevel.Low, other.Level);
        Assert.Equal(1.000m, other.SurgeMultiplier);
    }
}
