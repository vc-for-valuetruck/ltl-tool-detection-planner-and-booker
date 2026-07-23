using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.DispatchAssist;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the Dispatch Assist explainable ranking. Every assertion pins a behavior the demo and the
/// honest-data posture depend on: missing factors are excluded from the denominator (never penalized),
/// an off/available driver is still rankable (never a hard disqualifier), a dispatcher preference lifts
/// the score, and the proximity reason is always a labeled home-base reference — never fabricated GPS.
/// </summary>
public sealed class DispatchAssistServiceTests
{
    private static DispatchAssistService Service(LtlOptions? options = null) =>
        new(alvys: null!, loads: null!, LtlTestFactory.Options(options),
            NullLogger<DispatchAssistService>.Instance);

    private static AlvysDriver Driver(
        string id = "D1", string? state = "TX", string? status = "Active", bool? isActive = true) =>
        new()
        {
            Id = id,
            Name = $"Driver {id}",
            Email = $"{id}@fleet.test",
            Status = status,
            IsActive = isActive,
            Address = state is null ? null : new AlvysContextAddress { State = state },
        };

    private static DispatchTarget Target(
        string? originState = "TX", IReadOnlyList<string>? equipment = null) => new()
        {
            OriginState = originState,
            RequiredEquipment = equipment ?? [],
            Source = "test",
        };

    [Fact]
    public void Same_state_home_base_earns_full_proximity_and_is_labeled_as_reference()
    {
        var candidate = new RawCandidate(Driver(state: "TX"), null, null, null);

        var row = Service().Rank(candidate, Target(originState: "TX"));

        Assert.Equal("TX", row.DriverHomeState);
        Assert.Equal(60, row.ReferenceMilesFromOrigin); // intra-state nominal reference (see ReferenceMiles)
        Assert.Contains(row.Reasons, r => r.Contains("home base TX matches origin state"));
    }

    [Fact]
    public void Distant_home_base_scores_lower_than_near_home_base()
    {
        var near = new RawCandidate(Driver(id: "near", state: "OK"), null, null, null); // OK adjacent to TX
        var far = new RawCandidate(Driver(id: "far", state: "ME"), null, null, null);   // Maine, far from TX

        var svc = Service();
        var nearRow = svc.Rank(near, Target(originState: "TX"));
        var farRow = svc.Rank(far, Target(originState: "TX"));

        Assert.True(nearRow.ReferenceMilesFromOrigin < farRow.ReferenceMilesFromOrigin);
        Assert.True(nearRow.Score > farRow.Score);
        Assert.Contains(nearRow.Reasons, r => r.Contains("home-base reference"));
    }

    [Fact]
    public void Missing_origin_state_excludes_proximity_rather_than_scoring_it_zero()
    {
        // An active driver whose proximity is unavailable (no origin state) must still score on its
        // other factors — the missing factor is dropped from the denominator, never coerced to a
        // zero-mile "near" nor dragged down as a zero-credit "far".
        var candidate = new RawCandidate(Driver(state: "TX"), null, null, null);

        var row = Service().Rank(candidate, Target(originState: null));

        Assert.Contains(row.Reasons, r => r.Contains("proximity unavailable"));
        Assert.Null(row.ReferenceMilesFromOrigin);
        Assert.True(row.Score > 0);
    }

    [Fact]
    public void Off_duty_driver_is_still_rankable_never_a_hard_disqualifier()
    {
        var offDuty = new RawCandidate(
            Driver(status: "Off Duty", isActive: false), null, null, null);

        var row = Service().Rank(offDuty, Target());

        Assert.True(row.Score > 0);
        Assert.Contains(row.Reasons, r => r.Contains("may still be dispatchable"));
    }

    [Fact]
    public void Active_driver_outscores_the_same_driver_reported_inactive()
    {
        var svc = Service();
        var active = svc.Rank(new RawCandidate(Driver(isActive: true, status: "Active"), null, null, null), Target());
        var inactive = svc.Rank(new RawCandidate(Driver(isActive: false, status: "Off Duty"), null, null, null), Target());

        Assert.True(active.Score > inactive.Score);
    }

    [Fact]
    public void Dispatcher_preference_lifts_the_score_and_flags_the_pairing()
    {
        var svc = Service();
        var driver = Driver();
        var truck = new AlvysTruck { Id = "T1", TruckNum = "214" };
        var pref = new AlvysDispatchPreference { DispatcherId = "U9", Driver1Id = "D1", TruckId = "T1" };

        var withPref = svc.Rank(new RawCandidate(driver, truck, null, pref), Target());
        var withoutPref = svc.Rank(new RawCandidate(driver, truck, null, null), Target());

        Assert.True(withPref.IsPreferredPairing);
        Assert.False(withoutPref.IsPreferredPairing);
        Assert.True(withPref.Score > withoutPref.Score);
        Assert.Equal("U9", withPref.PreferredDispatcherId);
        Assert.Contains(withPref.Reasons, r => r.Contains("preferred pairing") && r.Contains("214"));
    }

    [Fact]
    public void Equipment_fit_is_scored_only_when_both_load_requirement_and_trailer_type_are_known()
    {
        var svc = Service();
        var driver = Driver();
        var reeferTrailer = new AlvysTrailerEquipment { Id = "R1", TrailerNum = "900", EquipmentType = "Reefer" };

        var fits = svc.Rank(
            new RawCandidate(driver, null, reeferTrailer, null),
            Target(equipment: ["Reefer"]));
        var mismatch = svc.Rank(
            new RawCandidate(driver, null, reeferTrailer, null),
            Target(equipment: ["Flatbed"]));

        Assert.Contains(fits.Reasons, r => r.Contains("fits load requirement"));
        Assert.Contains(mismatch.Reasons, r => r.Contains("may not fit"));
        Assert.True(fits.Score > mismatch.Score);
    }

    [Fact]
    public void Score_is_bounded_zero_to_one_hundred()
    {
        var svc = Service();
        var driver = Driver();
        var truck = new AlvysTruck { Id = "T1", TruckNum = "214" };
        var trailer = new AlvysTrailerEquipment { Id = "R1", TrailerNum = "900", EquipmentType = "Reefer" };
        var pref = new AlvysDispatchPreference { DispatcherId = "U9", Driver1Id = "D1", TruckId = "T1", TrailerId = "R1" };

        var row = svc.Rank(
            new RawCandidate(driver, truck, trailer, pref),
            Target(originState: "TX", equipment: ["Reefer"]));

        Assert.InRange(row.Score, 0, 100);
        Assert.Equal("D1@fleet.test", row.DriverEmail);
    }
}
