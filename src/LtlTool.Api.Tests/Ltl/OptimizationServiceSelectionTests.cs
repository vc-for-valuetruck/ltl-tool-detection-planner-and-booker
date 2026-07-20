using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the Phase 2 optimization scaffolding: with every <c>Ltl:Optimization:*</c> flag
/// off (the default), <see cref="LtlServiceCollectionExtensions.AddLtlDecisionSupport"/> registers
/// the <c>Null…</c> engines, and each null engine degrades honestly rather than fabricating a
/// verdict/plan/sequence.
/// </summary>
public sealed class OptimizationServiceSelectionTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLtlDecisionSupport(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_config_registers_all_null_optimization_engines()
    {
        using var provider = BuildProvider([]);

        Assert.IsType<NullTrailerFitService>(provider.GetRequiredService<ITrailerFitService>());
        Assert.IsType<NullCapacityCostSolver>(provider.GetRequiredService<ICapacityCostSolver>());
        Assert.IsType<NullStopSequencer>(provider.GetRequiredService<IStopSequencer>());

        Assert.False(provider.GetRequiredService<ITrailerFitService>().IsEnabled);
        Assert.False(provider.GetRequiredService<ICapacityCostSolver>().IsEnabled);
        Assert.False(provider.GetRequiredService<IStopSequencer>().IsEnabled);
    }

    [Fact]
    public async Task NullTrailerFitService_returns_unknown_verdict()
    {
        var svc = new NullTrailerFitService(TimeProvider.System);
        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Unknown, result.Verdict);
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public async Task NullCapacityCostSolver_returns_unsolved()
    {
        var svc = new NullCapacityCostSolver(TimeProvider.System);
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new CapacityCostCandidate("L-1", 12_000m, 8, 1_000m, 250m)]);

        var result = await svc.SolveAsync(request);

        Assert.False(result.Solved);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task NullStopSequencer_preserves_input_order()
    {
        var svc = new NullStopSequencer(TimeProvider.System);
        var request = new StopSequenceRequest([
            new StopToSequence("S1", "Laredo", "TX", null, null),
            new StopToSequence("S2", "Dallas", "TX", null, null),
        ]);

        var result = await svc.SequenceAsync(request);

        Assert.False(result.Optimized);
        Assert.Equal(["S1", "S2"], result.OrderedStopRefs);
    }
}
