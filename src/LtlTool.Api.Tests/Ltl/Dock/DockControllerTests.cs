using System.Security.Claims;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Dock;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Dock;

/// <summary>
/// Direct-construction coverage for the Dock controller's own logic: the honest 200 warehouse
/// projection and the 400 conversions (missing parentLoadId, bad-request combine). The deeper
/// combine/candidate behavior is covered by <see cref="DockServiceTests"/>.
/// </summary>
public sealed class DockControllerTests
{
    private static DockController BuildController(FakeAlvysClient client, ConsolidationOptions? overrides = null)
    {
        var opts = overrides ?? new ConsolidationOptions();
        var optsWrap = Microsoft.Extensions.Options.Options.Create(opts);

        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());

        var arrivals = new LaredoArrivalsService(
            client, LtlTestFactory.Options(), optsWrap, LtlTestFactory.Clock());
        var candidates = new ConsolidationCandidateService(
            loads, optsWrap, LtlTestFactory.Options(), LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(opts));
        var plans = new ConsolidationPlanService(
            loads, optsWrap, LtlTestFactory.Options(), LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(opts),
            new NullTrailerFitService(TimeProvider.System),
            new NullStopSequencer(LtlTestFactory.Clock()));
        var audits = new InMemoryConsolidationAuditStore(LtlTestFactory.Clock());

        var controller = new DockController(new DockService(arrivals, candidates, plans, audits, optsWrap));
        var identity = new ClaimsIdentity([new Claim("preferred_username", "dock@valuetruck.com")], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    [Fact]
    public void GetWarehouses_returns_the_configured_yards()
    {
        var result = BuildController(new FakeAlvysClient()).GetWarehouses();

        var response = Assert.IsType<DockWarehousesResponse>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(2, response.Warehouses.Count);
    }

    [Fact]
    public async Task GetCandidates_rejects_a_blank_parent_load_id()
    {
        var controller = BuildController(new FakeAlvysClient());

        var result = await controller.GetCandidates(parentLoadId: "  ", corridor: null, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Combine_returns_400_when_the_plan_cannot_be_built()
    {
        var controller = BuildController(new FakeAlvysClient());

        var result = await controller.Combine(
            new DockCombineRequest { ParentLoadId = "", SiblingLoadIds = ["L-2"] }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
