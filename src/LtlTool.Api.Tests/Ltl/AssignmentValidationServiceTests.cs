using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Assignment;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the internal assignment validator: hard rules block, soft concerns warn, and a clean
/// proposal passes with no issues. Mirrors the read-only boundary — nothing here touches Alvys.
/// </summary>
public sealed class AssignmentValidationServiceTests
{
    private static AssignmentValidationService Service() =>
        new(LtlTestFactory.Options(), LtlTestFactory.Clock());

    private static LtlLoadSummary Load(
        decimal? weight = 8000m,
        IReadOnlyList<string>? equipment = null,
        IReadOnlyList<MissingDataFlag>? missing = null,
        DateTimeOffset? pickup = null) => new()
    {
        Id = "L1",
        Status = "Open",
        Equipment = equipment ?? ["Dry Van"],
        WeightLbs = weight,
        Origin = new LtlPlace { City = "Dallas", State = "TX" },
        ScheduledPickupAt = pickup,
        MissingData = missing ?? [],
    };

    private static AlvysDriver GoodDriver() => new()
    {
        Id = "DR1",
        Name = "Sam",
        IsActive = true,
        LicenseExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        MedicalExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static AlvysTrailerEquipment Trailer(string type = "Dry Van", decimal capacity = 40000m) => new()
    {
        Id = "TR1",
        EquipmentType = type,
        Capacity = new AlvysTrailerCapacity { Weight = capacity },
    };

    [Fact]
    public void Clean_proposal_has_no_issues()
    {
        var request = new AssignmentRequest { DriverId = "DR1", TrailerId = "TR1" };
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = Trailer() };

        var result = Service().Validate(Load(), request, candidate);

        Assert.False(result.HasBlockers);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void No_driver_is_a_blocker()
    {
        var result = Service().Validate(Load(), new AssignmentRequest(), new MatchCandidate());

        Assert.True(result.HasBlockers);
        Assert.Contains(result.Blockers, i => i.Code == "NO_DRIVER");
    }

    [Fact]
    public void Terminated_driver_blocks()
    {
        var driver = GoodDriver();
        driver.TerminatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new AssignmentRequest { DriverId = "DR1" };

        var result = Service().Validate(Load(), request, new MatchCandidate { Driver = driver });

        Assert.Contains(result.Blockers, i => i.Code == "DRIVER_TERMINATED");
    }

    [Fact]
    public void Expired_license_blocks()
    {
        var driver = GoodDriver();
        driver.LicenseExpiresAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new AssignmentRequest { DriverId = "DR1" };

        var result = Service().Validate(Load(), request, new MatchCandidate { Driver = driver });

        Assert.Contains(result.Blockers, i => i.Code == "LICENSE_EXPIRED");
    }

    [Fact]
    public void Over_capacity_blocks()
    {
        var request = new AssignmentRequest { DriverId = "DR1", TrailerId = "TR1" };
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = Trailer(capacity: 5000m) };

        var result = Service().Validate(Load(weight: 8000m), request, candidate);

        Assert.Contains(result.Blockers, i => i.Code == "OVER_CAPACITY");
    }

    [Fact]
    public void Equipment_mismatch_is_a_warning_not_a_blocker()
    {
        var request = new AssignmentRequest { DriverId = "DR1", TrailerId = "TR1" };
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = Trailer(type: "Reefer") };

        var result = Service().Validate(Load(equipment: ["Dry Van"]), request, candidate);

        Assert.False(result.HasBlockers);
        Assert.Contains(result.Warnings, i => i.Code == "EQUIPMENT_MISMATCH");
    }

    [Fact]
    public void Unresolved_driver_warns_but_does_not_block()
    {
        var request = new AssignmentRequest { DriverId = "DR-unknown" };

        var result = Service().Validate(Load(), request, new MatchCandidate());

        Assert.False(result.HasBlockers);
        Assert.Contains(result.Warnings, i => i.Code == "DRIVER_UNRESOLVED");
    }

    [Fact]
    public void Missing_rate_and_weight_surface_as_warnings()
    {
        var request = new AssignmentRequest { DriverId = "DR1" };
        var load = Load(weight: null, missing: [MissingDataFlag.Rate, MissingDataFlag.Weight]);

        var result = Service().Validate(load, request, new MatchCandidate { Driver = GoodDriver() });

        Assert.Contains(result.Warnings, i => i.Code == "MISSING_RATE");
        Assert.Contains(result.Warnings, i => i.Code == "MISSING_WEIGHT");
    }

    [Fact]
    public void Past_pickup_window_warns()
    {
        var request = new AssignmentRequest { DriverId = "DR1" };
        var load = Load(pickup: LtlTestFactory.Now.AddDays(-3));

        var result = Service().Validate(load, request, new MatchCandidate { Driver = GoodDriver() });

        Assert.Contains(result.Warnings, i => i.Code == "PICKUP_WINDOW_PASSED");
    }
}
