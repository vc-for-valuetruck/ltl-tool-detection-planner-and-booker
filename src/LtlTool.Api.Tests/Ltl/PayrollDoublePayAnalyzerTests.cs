using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the payroll double-pay guard (Phase 4): the highest-value billing-leak check. A driver
/// paid non-zero loaded miles on more than one trip of the same consolidation group (linked by the
/// Alvys <c>Main Load Id</c> trip reference) is flagged; a properly-zeroed child trip, an
/// unidentifiable driver, and a set with no consolidation reference are all honestly not findings.
/// </summary>
public sealed class PayrollDoublePayAnalyzerTests
{
    private static AlvysTrip Trip(
        string id,
        string? mainLoadId,
        (string? Id, string? Name)? driver = null,
        decimal? loadedMiles = null,
        decimal? tripValue = null,
        string? loadNumber = null,
        bool ltlMarker = true)
    {
        var references = new List<AlvysReference>();
        if (ltlMarker) references.Add(new AlvysReference { Name = "LTL", Value = "true" });
        if (mainLoadId is not null)
            references.Add(new AlvysReference { Name = "Main Load Id", Value = mainLoadId });

        return new AlvysTrip
        {
            Id = id,
            LoadNumber = loadNumber ?? id,
            References = references,
            LoadedMileage = loadedMiles is null
                ? null
                : new AlvysDistanceMeasurement { Distance = new AlvysDistance { Value = loadedMiles } },
            TripValue = tripValue is null ? null : new AlvysMoney { Amount = tripValue },
            Driver = driver is null ? null : new AlvysPartyPay { Id = driver.Value.Id, Name = driver.Value.Name },
        };
    }

    // ── NotEvaluated semantics ──────────────────────────────────────────

    [Fact]
    public void Empty_input_is_NotEvaluated()
    {
        var result = new PayrollDoublePayAnalyzer().Analyze([]);
        Assert.Same(PayrollDoublePayResult.NotEvaluated, result);
        Assert.False(result.Evaluated);
    }

    [Fact]
    public void Trips_without_a_main_load_id_reference_are_NotEvaluated()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: null, driver: ("D1", "Ann"), loadedMiles: 500, ltlMarker: false),
            Trip("T2", mainLoadId: null, driver: ("D1", "Ann"), loadedMiles: 500, ltlMarker: false),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.False(result.Evaluated);
        Assert.False(result.HasDoublePayRisk);
    }

    // ── The core double-pay detection ───────────────────────────────────

    [Fact]
    public void Same_driver_on_two_sibling_trips_with_nonzero_miles_is_flagged()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480, tripValue: 900),
            Trip("T2", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480, tripValue: 900),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.True(result.Evaluated);
        Assert.True(result.HasDoublePayRisk);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("P-100", finding.ParentLoadId);
        Assert.Equal("D1", finding.DriverId);
        Assert.Equal("Ann", finding.DriverName);
        Assert.Equal(2, finding.Trips.Count);
        Assert.Contains(finding.Trips, t => t.TripId == "T1");
        Assert.Contains(finding.Trips, t => t.TripId == "T2");
    }

    [Fact]
    public void Three_sibling_trips_paid_triple_are_flagged_with_all_three()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T3", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(3, finding.Trips.Count);
    }

    [Fact]
    public void Zeroed_child_trip_is_not_a_double_pay()
    {
        // The main trip pays the miles; the child was correctly zeroed. No leak.
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 0),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.True(result.Evaluated);
        Assert.False(result.HasDoublePayRisk);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Missing_loaded_miles_is_not_counted_as_a_charge()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: null),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.False(result.HasDoublePayRisk);
    }

    [Fact]
    public void Different_drivers_on_siblings_are_not_a_double_pay()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: ("D2", "Bob"), loadedMiles: 480),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.True(result.Evaluated);
        Assert.False(result.HasDoublePayRisk);
    }

    [Fact]
    public void Trips_in_different_groups_do_not_cross_contaminate()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-200", driver: ("D1", "Ann"), loadedMiles: 480),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        // Same driver, but on two DIFFERENT consolidation groups — that's two real linehauls.
        Assert.False(result.HasDoublePayRisk);
    }

    [Fact]
    public void Driver_with_no_id_or_name_is_not_attributable()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: (null, null), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: (null, null), loadedMiles: 480),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        Assert.False(result.HasDoublePayRisk);
    }

    [Fact]
    public void Driver_matched_by_name_when_id_absent()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: (null, "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: (null, "Ann"), loadedMiles: 480),
        };

        var result = new PayrollDoublePayAnalyzer().Analyze(trips);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Ann", finding.DriverId);
    }

    [Fact]
    public void Same_driver_on_secondary_slots_counts_once_per_trip()
    {
        // A single trip listing the same driver in both Driver and Driver1 must not self-flag.
        var trip = new AlvysTrip
        {
            Id = "T1",
            LoadNumber = "T1",
            References = [new AlvysReference { Name = "Main Load Id", Value = "P-100" }],
            LoadedMileage = new AlvysDistanceMeasurement { Distance = new AlvysDistance { Value = 480 } },
            Driver = new AlvysPartyPay { Id = "D1", Name = "Ann" },
            Driver1 = new AlvysPartyPay { Id = "D1", Name = "Ann" },
        };

        var result = new PayrollDoublePayAnalyzer().Analyze([trip]);

        Assert.False(result.HasDoublePayRisk);
    }

    [Fact]
    public void Finding_message_names_the_driver_and_group()
    {
        var trips = new[]
        {
            Trip("T1", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
            Trip("T2", mainLoadId: "P-100", driver: ("D1", "Ann"), loadedMiles: 480),
        };

        var finding = Assert.Single(new PayrollDoublePayAnalyzer().Analyze(trips).Findings);

        Assert.Contains("Ann", finding.Message);
        Assert.Contains("P-100", finding.Message);
    }
}
