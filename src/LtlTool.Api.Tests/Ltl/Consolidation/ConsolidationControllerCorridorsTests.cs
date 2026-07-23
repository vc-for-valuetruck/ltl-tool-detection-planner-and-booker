using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Corridors endpoint is a small read-only projection of static config, so a direct
/// controller construction gives clean coverage. WebApplicationFactory is overkill for a
/// method that never touches services, DB, Alvys, or auth.
/// </summary>
public class ConsolidationControllerCorridorsTests
{
    [Fact]
    public void GetCorridors_projects_configured_corridors_with_warehouse_details()
    {
        var options = new ConsolidationOptions
        {
            Warehouses =
            [
                new() { Code = "LAREDO", Name = "Laredo yard", State = "TX", NearbyCities = ["Laredo"] },
                new() { Code = "DALLAS", Name = "Dallas 154-door yard", State = "TX", NearbyCities = ["Dallas", "Fort Worth"] },
            ],
            Corridors =
            [
                new() { Code = "LAREDO_TO_DALLAS", OriginWarehouseCode = "LAREDO", DestinationWarehouseCode = "DALLAS", PickupWindowDays = 2, DeliveryWindowDays = 3 },
            ],
        };
        var controller = BuildController(options);

        var result = controller.GetCorridors();
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<CorridorSummary>>(
            ((OkObjectResult)result.Result!).Value);

        var only = Assert.Single(summaries);
        Assert.Equal("LAREDO_TO_DALLAS", only.Code);
        Assert.Equal("LAREDO", only.Origin.Code);
        Assert.Equal("Laredo yard", only.Origin.Name);
        Assert.Contains("Laredo", only.Origin.NearbyCities);
        Assert.Equal("DALLAS", only.Destination.Code);
        Assert.Contains("Fort Worth", only.Destination.NearbyCities);
        Assert.Equal(2, only.PickupWindowDays);
        Assert.Equal(3, only.DeliveryWindowDays);
    }

    [Fact]
    public void GetCorridors_skips_corridor_when_a_referenced_warehouse_is_missing()
    {
        // Misconfiguration: corridor references a warehouse that doesn't exist. Rather than
        // fail loudly and take the whole endpoint down, drop the bad entry and return the
        // rest. The unknown-corridor 400 on /candidates + /plan is the real enforcement point.
        var options = new ConsolidationOptions
        {
            Warehouses =
            [
                new() { Code = "LAREDO", Name = "Laredo yard", State = "TX", NearbyCities = ["Laredo"] },
            ],
            Corridors =
            [
                new() { Code = "LAREDO_TO_UNKNOWN", OriginWarehouseCode = "LAREDO", DestinationWarehouseCode = "GHOST" },
                new() { Code = "GHOST_TO_LAREDO",   OriginWarehouseCode = "GHOST",  DestinationWarehouseCode = "LAREDO" },
            ],
        };
        var controller = BuildController(options);

        var result = controller.GetCorridors();
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<CorridorSummary>>(
            ((OkObjectResult)result.Result!).Value);

        Assert.Empty(summaries);
    }

    [Fact]
    public void GetCorridors_returns_empty_list_when_no_corridors_configured()
    {
        var controller = BuildController(new ConsolidationOptions
        {
            Corridors = [],
        });
        var result = controller.GetCorridors();
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<CorridorSummary>>(
            ((OkObjectResult)result.Result!).Value);
        Assert.Empty(summaries);
    }

    private static ConsolidationController BuildController(ConsolidationOptions options)
    {
        // /corridors doesn't touch candidates/plans/audits/corridor-health; nulls are fine for this
        // direct-construction test. The corridor-health sweep is covered by CorridorHealthProbe tests.
        return new ConsolidationController(
            candidates: null!,
            plans: null!,
            audits: null!,
            laneTemplates: null!,
            options: Microsoft.Extensions.Options.Options.Create(options),
            corridorHealth: null!,
            autoExecute: null!,
            logger: NullLogger<ConsolidationController>.Instance);
    }
}
