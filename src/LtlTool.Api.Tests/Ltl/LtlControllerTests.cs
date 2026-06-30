using System.Security.Claims;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Assignment;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the LTL controller: search/detail wiring, 404 on missing loads, and the internal
/// assignment-audit boundary that explicitly does not write back to Alvys.
/// </summary>
public sealed class LtlControllerTests
{
    private static LtlController Build(FakeAlvysClient client, IAssignmentAuditStore? store = null, string? user = "dispatcher@valuetruck.com")
    {
        var options = LtlTestFactory.Options();
        var normalizer = LtlTestFactory.Normalizer();
        var loadService = new LtlLoadService(client, normalizer, options);
        var matchService = new MatchService(client, LtlTestFactory.Scorer(), options);
        var controller = new LtlController(loadService, matchService, store ?? new InMemoryAssignmentAuditStore());

        var identity = user is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim("preferred_username", user)], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private static T Body<T>(ActionResult<T> result)
        => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Search_returns_paged_normalized_loads()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Open", CustomerName = "Acme" },
                new AlvysLoad { Id = "L2", LoadNumber = "101", Status = "Delivered", CustomerName = "Globex" },
            ],
        };

        var body = Body(await Build(client).Search(new LtlSearchQuery(), default));

        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Items.Count);
    }

    [Fact]
    public async Task GetLoad_returns_404_when_not_found()
    {
        var client = new FakeAlvysClient { LoadDetail = null };

        var result = await Build(client).GetLoad("missing", default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetMatches_returns_404_when_load_not_found()
    {
        var client = new FakeAlvysClient { LoadDetail = null };

        var result = await Build(client).GetMatches("missing", 5, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetMatches_ranks_candidates_for_a_load()
    {
        var client = new FakeAlvysClient
        {
            LoadDetail = new AlvysLoad { Id = "L1", Status = "Open", RequiredEquipment = ["Dry Van"], Weight = 8000m },
            Drivers = [new AlvysDriver { Id = "DR1", Name = "Sam", IsActive = true }],
            Trailers = [new AlvysTrailerEquipment { Id = "TR1", EquipmentType = "Dry Van", Capacity = new AlvysTrailerCapacity { Weight = 40000m } }],
            DispatchPreferences = [new AlvysDispatchPreference { Driver1Id = "DR1", TrailerId = "TR1" }],
        };

        var result = await Build(client).GetMatches("L1", 5, default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<MatchResult>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.NotEmpty(body);
        Assert.Equal("DR1", body[0].DriverId);
    }

    [Fact]
    public async Task Assign_records_audit_without_alvys_writeback()
    {
        var client = new FakeAlvysClient { LoadDetail = new AlvysLoad { Id = "L1", Status = "Open" } };
        var store = new InMemoryAssignmentAuditStore();
        var controller = Build(client, store);

        var request = new AssignmentRequest { DriverId = "DR1", MatchScore = 92, MatchLabel = "Excellent Match" };
        var result = await controller.Assign("L1", request, default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var audit = Assert.IsType<AssignmentAudit>(created.Value);
        Assert.Equal("NotPerformed", audit.AlvysWriteback);
        Assert.Equal("dispatcher@valuetruck.com", audit.RecordedBy);
        Assert.Equal("DR1", audit.DriverId);
        Assert.Single(store.ForLoad("L1"));
    }

    [Fact]
    public async Task Assign_returns_404_when_load_not_found()
    {
        var client = new FakeAlvysClient { LoadDetail = null };

        var result = await Build(client).Assign("missing", new AssignmentRequest(), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task BillingWorklist_excludes_already_invoiced_loads()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                new AlvysLoad { Id = "L1", Status = "Delivered", CustomerId = "C1", CustomerRate = 100m, Weight = 1000m, ActualDeliveryAt = LtlTestFactory.Now },
                new AlvysLoad { Id = "L2", Status = "Invoiced", CustomerId = "C2", CustomerRate = 200m, Weight = 2000m, InvoicedAt = LtlTestFactory.Now },
            ],
        };

        var result = await Build(client).BillingWorklist(null, default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<LtlLoadSummary>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.DoesNotContain(body, s => s.Id == "L2");
    }
}
