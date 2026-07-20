using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// DI wiring for the Phase 2 M3 OR-Tools capacity/cost optimization engines. Lives in its own file
/// (not the shared <c>LtlServiceCollectionExtensions</c>) to keep merge conflicts with parallel
/// Phase 2 branches minimal.
///
/// <para>
/// The capacity/cost solver and the stop sequencer are both gated behind
/// <c>Ltl:Optimization:Solver:Enabled</c> (default false). When off, the <c>Null…</c> engines are
/// registered — identical posture to the M1 scaffolding — so a fresh clone, CI, and the demo path
/// only ever run the no-op services and no half-built optimization can affect behavior.
/// </para>
/// </summary>
public static class OptimizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capacity/cost solver + stop sequencer (real when the Solver flag is on, else
    /// the <c>Null…</c> fallbacks) plus the shared distance-matrix provider and their options.
    /// Call this from the LTL composition root in place of the M1 <c>Null…</c> registrations for
    /// <see cref="ICapacityCostSolver"/> and <see cref="IStopSequencer"/>.
    /// </summary>
    public static IServiceCollection AddLtlCapacityCostOptimization(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CapacityCostSolverOptions>()
            .Bind(configuration.GetSection(CapacityCostSolverOptions.SectionName));
        services.AddOptions<StopSequencerOptions>()
            .Bind(configuration.GetSection(StopSequencerOptions.SectionName));
        services.AddOptions<DistanceMatrixOptions>()
            .Bind(configuration.GetSection(DistanceMatrixOptions.SectionName));

        // Deterministic clock everywhere else in the slice; match it here for testability.
        services.TryAddSingleton(TimeProvider.System);

        // Alvys-first distance matrix with an in-memory cache — shared by both engines.
        services.TryAddSingleton<IDistanceMatrixProvider, AlvysDistanceMatrixProvider>();

        var solverEnabled = configuration
            .GetSection(CapacityCostSolverOptions.SectionName)
            .GetValue<bool>(nameof(CapacityCostSolverOptions.Enabled));

        if (solverEnabled)
        {
            services.AddSingleton<ICapacityCostSolver, OrToolsCapacityCostSolver>();
            services.AddSingleton<IStopSequencer, OrToolsStopSequencer>();
        }
        else
        {
            services.AddSingleton<ICapacityCostSolver, NullCapacityCostSolver>();
            services.AddSingleton<IStopSequencer, NullStopSequencer>();
        }

        return services;
    }
}
