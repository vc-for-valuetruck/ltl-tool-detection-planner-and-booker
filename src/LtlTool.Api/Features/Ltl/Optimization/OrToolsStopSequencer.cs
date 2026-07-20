using Google.OrTools.ConstraintSolver;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>Tuning for the OR-Tools stop sequencer. Bound from <c>Ltl:Optimization:Solver</c>.</summary>
public sealed class StopSequencerOptions
{
    public const string SectionName = "Ltl:Optimization:Solver";

    /// <summary>Hard wall-clock budget for a single sequencing solve. Default 3s.</summary>
    public double SequencerTimeLimitSeconds { get; set; } = 3;
}

/// <summary>
/// Orders a consolidation plan's stops into a shortest-visiting route using an OR-Tools open-ended
/// TSP rooted at the first stop (the parent origin). Feeds <c>ConsolidationPlanService</c>'s
/// click-card waypoint order.
///
/// <para>
/// Honest degradation: reordering is only performed when at least two stops carry real coordinates,
/// because current Alvys reads expose only city/state on a stop. Without coordinates every leg would
/// be an identical estimate and any "optimization" would be fabricated — so the input order is
/// preserved and <see cref="StopSequenceResult.Optimized"/> is reported as false.
/// </para>
/// </summary>
public sealed class OrToolsStopSequencer(
    IDistanceMatrixProvider distances,
    IOptions<StopSequencerOptions> options,
    TimeProvider clock,
    ILogger<OrToolsStopSequencer> logger) : IStopSequencer
{
    private readonly StopSequencerOptions _opts = options.Value;

    public bool IsEnabled => true;

    public Task<StopSequenceResult> SequenceAsync(StopSequenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stops = request.Stops;
        var inputOrder = stops.Select(s => s.StopRef).ToList();

        var stopsWithCoords = stops.Count(s => s.Latitude is not null && s.Longitude is not null);
        if (stops.Count < 3 || stopsWithCoords < 2)
        {
            return Task.FromResult(new StopSequenceResult(
                inputOrder,
                Optimized: false,
                stops.Count < 3
                    ? "Fewer than three stops — input order preserved."
                    : "No stop coordinates available — input order preserved (Alvys exposes city/state only).",
                clock.GetUtcNow()));
        }

        var points = stops.Select(s => new GeoPoint(s.City, s.State, s.Latitude, s.Longitude)).ToList();
        var refs = stops.Select(s => s.StopRef).ToList();
        var matrix = distances.Build(points, refs, []);

        var manager = new RoutingIndexManager(stops.Count, 1, 0);
        var routing = new RoutingModel(manager);

        // Open-ended TSP: returning to the start node is free, so the route is a path, not a cycle.
        var costCallback = routing.RegisterTransitCallback((from, to) =>
        {
            var toNode = manager.IndexToNode(to);
            if (toNode == 0) return 0;
            return matrix.Miles[manager.IndexToNode(from), toNode];
        });
        routing.SetArcCostEvaluatorOfAllVehicles(costCallback);

        var searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
        searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
        searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.GuidedLocalSearch;
        searchParameters.TimeLimit = ToDuration(_opts.SequencerTimeLimitSeconds);

        Google.OrTools.ConstraintSolver.Assignment solution;
        try
        {
            solution = routing.SolveWithParameters(searchParameters);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OR-Tools stop sequencing threw: {Message}", ex.Message);
            return Task.FromResult(new StopSequenceResult(
                inputOrder, Optimized: false, $"Sequencer error — input order preserved: {ex.Message}", clock.GetUtcNow()));
        }

        if (solution is null)
        {
            return Task.FromResult(new StopSequenceResult(
                inputOrder, Optimized: false, "No route found — input order preserved.", clock.GetUtcNow()));
        }

        var ordered = new List<string>(stops.Count);
        var index = routing.Start(0);
        while (!routing.IsEnd(index))
        {
            ordered.Add(refs[manager.IndexToNode(index)]);
            index = solution.Value(routing.NextVar(index));
        }

        var reordered = !ordered.SequenceEqual(inputOrder);
        return Task.FromResult(new StopSequenceResult(
            ordered,
            Optimized: reordered,
            reordered
                ? "Stops sequenced by shortest route over available coordinates."
                : "Shortest route matches input order — no change.",
            clock.GetUtcNow()));
    }

    private static Duration ToDuration(double seconds)
    {
        if (seconds <= 0) seconds = 1;
        var whole = (long)Math.Floor(seconds);
        var nanos = (int)Math.Round((seconds - whole) * 1_000_000_000);
        return new Duration { Seconds = whole, Nanos = nanos };
    }
}
