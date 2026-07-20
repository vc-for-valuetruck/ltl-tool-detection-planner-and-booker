using System.Text.Json;
using LtlTool.Api.Features.Health;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Covers the anonymous <see cref="OptimizationHealthController"/> readiness probe: it must report
/// the optimization flag states, the trailer-fit sidecar reachability, and the in-memory solver
/// self-test outcome — and mark the environment "degraded" only when an <i>enabled</i> component
/// fails its check. The UAT health workflow relies on this shape to prove optimization is actually on.
/// </summary>
public sealed class OptimizationHealthControllerTests
{
    private static readonly TimeProvider Clock = new FixedTimeProvider(LtlTestFactory.Now);

    private static JsonElement Invoke(
        OptimizationOptions optimization,
        ITrailerFitService trailerFit,
        ICapacityCostSolver solver,
        ITrailerFitClient? client)
    {
        // Register the client only when one is supplied, so the "flag on but no client" path is testable.
        var collection = new ServiceCollection();
        if (client is not null) collection.AddSingleton(client);
        var services = collection.BuildServiceProvider();

        var options = Microsoft.Extensions.Options.Options.Create(new LtlOptions { Optimization = optimization });
        var controller = new OptimizationHealthController(options, trailerFit, solver, services, Clock);

        var action = controller.Get(default).GetAwaiter().GetResult();
        var ok = Assert.IsType<OkObjectResult>(action);
        // Mirror ASP.NET's camelCase JSON output so nested record members (Reachable/Passed) match
        // the property names the health workflow parses.
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void All_flags_off_reports_ok_and_null_engine_details()
    {
        var body = Invoke(
            new OptimizationOptions(),
            new NullTrailerFitService(Clock),
            new NullCapacityCostSolver(Clock),
            client: null);

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("flags").GetProperty("trailerFit").GetBoolean());
        Assert.False(body.GetProperty("flags").GetProperty("solver").GetBoolean());
        Assert.False(body.GetProperty("flags").GetProperty("agentCommands").GetBoolean());
    }

    [Fact]
    public void All_flags_on_and_healthy_reports_ok()
    {
        var optimization = new OptimizationOptions
        {
            TrailerFit = new TrailerFitOptions { Enabled = true },
            Solver = new OptimizationFeatureToggle { Enabled = true },
            AgentCommands = new OptimizationFeatureToggle { Enabled = true },
        };

        var body = Invoke(
            optimization,
            new FakeTrailerFitService(enabled: true),
            new FakeSolver(solved: true),
            new FakePingClient(reachable: true));

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("flags").GetProperty("trailerFit").GetBoolean());
        Assert.True(body.GetProperty("flags").GetProperty("solver").GetBoolean());
        Assert.True(body.GetProperty("flags").GetProperty("agentCommands").GetBoolean());
        Assert.True(body.GetProperty("trailerFit").GetProperty("reachable").GetBoolean());
        Assert.True(body.GetProperty("solver").GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Trailer_fit_flag_on_but_sidecar_unreachable_is_degraded()
    {
        var optimization = new OptimizationOptions
        {
            TrailerFit = new TrailerFitOptions { Enabled = true },
        };

        var body = Invoke(
            optimization,
            new FakeTrailerFitService(enabled: true),
            new NullCapacityCostSolver(Clock),
            new FakePingClient(reachable: false));

        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("trailerFit").GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public void Solver_flag_on_but_unsolved_is_degraded()
    {
        var optimization = new OptimizationOptions
        {
            Solver = new OptimizationFeatureToggle { Enabled = true },
        };

        var body = Invoke(
            optimization,
            new NullTrailerFitService(Clock),
            new FakeSolver(solved: false),
            client: null);

        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("solver").GetProperty("passed").GetBoolean());
    }

    private sealed class FakeTrailerFitService(bool enabled) : ITrailerFitService
    {
        public bool IsEnabled => enabled;

        public Task<TrailerFitResult> EvaluateAsync(TrailerFitRequest request, CancellationToken ct = default)
            => Task.FromResult(new TrailerFitResult(TrailerFitVerdict.Unknown, "n/a", Clock.GetUtcNow()));
    }

    private sealed class FakeSolver(bool solved) : ICapacityCostSolver
    {
        public bool IsEnabled => true;

        public Task<CapacityCostResult> SolveAsync(CapacityCostRequest request, CancellationToken ct = default)
            => Task.FromResult(new CapacityCostResult(
                solved,
                solved ? new CapacityCostPlan(["__smoke_a__", "__smoke_b__"], 42m) : null,
                "test",
                Clock.GetUtcNow()));
    }

    private sealed class FakePingClient(bool reachable) : ITrailerFitClient
    {
        public Task<TrailerFitPlanSummary?> OptimizeAsync(TrailerFitOptimizeRequest request, CancellationToken ct)
            => Task.FromResult<TrailerFitPlanSummary?>(null);

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(reachable);
    }
}
