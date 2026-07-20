using Google.OrTools.ConstraintSolver;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>Tuning for the OR-Tools capacity/cost solver. Bound from <c>Ltl:Optimization:Solver</c>.</summary>
public sealed class CapacityCostSolverOptions
{
    public const string SectionName = "Ltl:Optimization:Solver";

    /// <summary>Master switch. When false the <c>NullCapacityCostSolver</c> is registered instead.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hard wall-clock budget for a single solve. Bounds latency on large sweeps. Default 5s.</summary>
    public double TimeLimitSeconds { get; set; } = 5;

    /// <summary>Fixed cost charged once for putting the trailer on the road (SetFixedCostOfVehicle).</summary>
    public decimal FixedVehicleCost { get; set; } = 250m;

    /// <summary>Variable cost per routed mile (arc cost).</summary>
    public decimal PerMileCost { get; set; } = 1.85m;

    /// <summary>
    /// Weight applied to a load's revenue when pricing the penalty for leaving it unconsolidated
    /// (AddDisjunction). Higher ⇒ the objective favors pulling more uplift onto the trailer.
    /// </summary>
    public double UpliftWeight { get; set; } = 1.0;

    /// <summary>
    /// Standard-trailer weight envelope (lbs) used when ranking opportunities before a specific
    /// Alvys trailer is assigned. A 53' dry van planning assumption, not an Alvys value — the
    /// solver rationale labels capacity as an assumption in this mode. Set null to leave weight
    /// unconstrained. Once a real <see cref="TrailerCapacitySpec"/> is available it always wins.
    /// </summary>
    public decimal? DefaultTrailerWeightLbs { get; set; } = 45_000m;

    /// <summary>Standard-trailer pallet envelope used for opportunity ranking. Planning assumption, not Alvys data.</summary>
    public int? DefaultTrailerPallets { get; set; } = 26;
}

/// <summary>
/// Capacity/cost solver backed by Google OR-Tools. Models the consolidation decision as a
/// single-trailer pickup-and-delivery routing problem: each candidate load is a pickup→delivery
/// pair, weight and pallets are capacity dimensions taken from <see cref="TrailerCapacitySpec"/>,
/// and each optional load carries an <c>AddDisjunction</c> penalty so it may stay unconsolidated at
/// a cost. The objective minimizes (arc cost + fixed vehicle cost + dropped-load penalties), which
/// — because a dropped load's penalty is priced from its revenue — favors pulling uplift onto the
/// trailer while respecting capacity by construction.
///
/// <para>
/// Pure compute over Alvys-derived inputs: distances come from <see cref="IDistanceMatrixProvider"/>
/// (Alvys leg miles first, clearly-labeled estimate otherwise). The solver never fetches data and
/// never invents a plan — an infeasible or empty problem returns <see cref="CapacityCostResult.Solved"/>
/// = false.
/// </para>
/// </summary>
public sealed class OrToolsCapacityCostSolver(
    IDistanceMatrixProvider distances,
    IOptions<CapacityCostSolverOptions> options,
    TimeProvider clock,
    ILogger<OrToolsCapacityCostSolver> logger) : ICapacityCostSolver
{
    private const int MoneyScale = 100; // cents — keeps decimal money exact as OR-Tools longs

    private readonly CapacityCostSolverOptions _opts = options.Value;

    public bool IsEnabled => true;

    public Task<CapacityCostResult> SolveAsync(CapacityCostRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Candidates.Count == 0)
        {
            return Task.FromResult(Unsolved("No candidate loads supplied — nothing to solve."));
        }

        // Node layout: 0 = depot; then per candidate k, pickup = 1+2k, delivery = 2+2k.
        var n = request.Candidates.Count;
        var nodeCount = 1 + (2 * n);

        var points = new List<GeoPoint>(nodeCount) { request.Depot ?? new GeoPoint(null, null) };
        var refs = new List<string>(nodeCount) { "__depot__" };
        var knownLegs = new List<KnownLeg>();

        for (var k = 0; k < n; k++)
        {
            var c = request.Candidates[k];
            var pickupRef = $"P:{c.LoadRef}";
            var deliveryRef = $"D:{c.LoadRef}";
            points.Add(c.Origin ?? new GeoPoint(null, null));
            refs.Add(pickupRef);
            points.Add(c.Destination ?? new GeoPoint(null, null));
            refs.Add(deliveryRef);

            // The load's own origin→destination miles are Alvys truth when present.
            if (c.Miles is > 0)
                knownLegs.Add(new KnownLeg(pickupRef, deliveryRef, c.Miles.Value));
        }

        var matrix = distances.Build(points, refs, knownLegs);

        var manager = new RoutingIndexManager(nodeCount, 1, 0);
        var routing = new RoutingModel(manager);

        // Pure miles callback (for the ordering dimension) and a cost callback (miles × per-mile cost).
        var perMileScaled = (long)Math.Round(_opts.PerMileCost * MoneyScale, MidpointRounding.AwayFromZero);
        var milesCallback = routing.RegisterTransitCallback((from, to) =>
            matrix.Miles[manager.IndexToNode(from), manager.IndexToNode(to)]);
        var costCallback = routing.RegisterTransitCallback((from, to) =>
            matrix.Miles[manager.IndexToNode(from), manager.IndexToNode(to)] * perMileScaled);

        routing.SetArcCostEvaluatorOfAllVehicles(costCallback);
        routing.SetFixedCostOfVehicle(
            (long)Math.Round(_opts.FixedVehicleCost * MoneyScale, MidpointRounding.AwayFromZero), 0);

        // Ordering dimension so a load's pickup precedes its delivery.
        routing.AddDimension(milesCallback, 0, long.MaxValue / 4, fix_start_cumul_to_zero: true, "Distance");
        var distanceDim = routing.GetDimensionOrDie("Distance");

        AddCapacityDimension(
            routing, manager, "Weight",
            request.Trailer.MaxWeightLbs,
            k => request.Candidates[k].WeightLbs is { } w ? (long)Math.Round(w, MidpointRounding.AwayFromZero) : 0L);

        AddCapacityDimension(
            routing, manager, "Pallets",
            request.Trailer.MaxPallets.HasValue ? request.Trailer.MaxPallets.Value : (decimal?)null,
            k => request.Candidates[k].Pallets ?? 0L);

        var pickupIndices = new long[n];
        var deliveryIndices = new long[n];
        for (var k = 0; k < n; k++)
        {
            var pickupIndex = manager.NodeToIndex(1 + (2 * k));
            var deliveryIndex = manager.NodeToIndex(2 + (2 * k));
            pickupIndices[k] = pickupIndex;
            deliveryIndices[k] = deliveryIndex;

            routing.AddPickupAndDelivery(pickupIndex, deliveryIndex);
            routing.solver().Add(routing.VehicleVar(pickupIndex) == routing.VehicleVar(deliveryIndex));
            routing.solver().Add(distanceDim.CumulVar(pickupIndex) <= distanceDim.CumulVar(deliveryIndex));

            var c = request.Candidates[k];
            if (!c.Mandatory)
            {
                // Penalty for leaving this load off the trailer: its revenue, scaled by the uplift
                // weight. When revenue is unknown we cannot value the uplift, so the penalty is 0 and
                // the load is dropped unless it happens to be free to carry — surfaced in the rationale.
                var penalty = c.Revenue is { } rev
                    ? (long)Math.Round((decimal)_opts.UpliftWeight * rev * MoneyScale, MidpointRounding.AwayFromZero)
                    : 0L;
                // max_cardinality: 2 so BOTH the pickup and delivery of this pair may be served — the
                // 2-arg overload defaults to 1, which would forbid serving the full pair and force every
                // optional load to be dropped. The penalty applies only when neither node is served.
                routing.AddDisjunction([pickupIndex, deliveryIndex], penalty, 2);
            }
        }

        // Consolidation semantics: every selected load rides the SAME trailer at the SAME time, so all
        // pickups must precede all deliveries. Without this, the router is free to pick-up→deliver→
        // pick-up sequentially, which respects the momentary capacity dimension while packing more total
        // weight than a single trailer can physically hold. Forcing every pickup before every delivery
        // makes the weight/pallet peak equal the sum of the selected loads — the true "does it all fit
        // on one trailer?" constraint the opportunity ranking needs.
        for (var p = 0; p < n; p++)
        {
            for (var d = 0; d < n; d++)
            {
                routing.solver().Add(
                    distanceDim.CumulVar(pickupIndices[p]) <= distanceDim.CumulVar(deliveryIndices[d]));
            }
        }

        var searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
        searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
        searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.GuidedLocalSearch;
        searchParameters.TimeLimit = ToDuration(_opts.TimeLimitSeconds);

        Google.OrTools.ConstraintSolver.Assignment solution;
        try
        {
            solution = routing.SolveWithParameters(searchParameters);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OR-Tools capacity/cost solve threw: {Message}", ex.Message);
            return Task.FromResult(Unsolved($"Solver error: {ex.Message}"));
        }

        if (solution is null)
        {
            return Task.FromResult(Unsolved("No feasible consolidation plan within the capacity envelope."));
        }

        var selected = new List<string>();
        decimal usedWeight = 0m;
        var usedPallets = 0;
        for (var k = 0; k < n; k++)
        {
            var pickupIndex = manager.NodeToIndex(1 + (2 * k));
            var dropped = solution.Value(routing.NextVar(pickupIndex)) == pickupIndex;
            if (dropped) continue;

            var c = request.Candidates[k];
            selected.Add(c.LoadRef);
            usedWeight += c.WeightLbs ?? 0m;
            usedPallets += c.Pallets ?? 0;
        }

        var objective = decimal.Divide(solution.ObjectiveValue(), MoneyScale);
        var rationale = BuildRationale(request, selected.Count, usedWeight, usedPallets, matrix.AnyEstimated);

        return Task.FromResult(new CapacityCostResult(
            Solved: true,
            Plan: new CapacityCostPlan(selected, objective),
            rationale,
            clock.GetUtcNow()));
    }

    private static void AddCapacityDimension(
        RoutingModel routing,
        RoutingIndexManager manager,
        string name,
        decimal? capacity,
        Func<int, long> demandForCandidate)
    {
        if (capacity is null or <= 0) return; // no capacity signal ⇒ dimension cannot be enforced

        var cap = (long)Math.Round(capacity.Value, MidpointRounding.AwayFromZero);
        var demandCallback = routing.RegisterUnaryTransitCallback(index =>
        {
            var node = manager.IndexToNode(index);
            if (node == 0) return 0; // depot
            var candidate = (node - 1) / 2;
            var isPickup = (node - 1) % 2 == 0;
            var demand = demandForCandidate(candidate);
            return isPickup ? demand : -demand;
        });

        routing.AddDimensionWithVehicleCapacity(
            demandCallback, slack_max: 0, vehicle_capacities: [cap], fix_start_cumul_to_zero: true, name);
    }

    private string BuildRationale(
        CapacityCostRequest request, int selectedCount, decimal usedWeight, int usedPallets, bool anyEstimated)
    {
        var parts = new List<string>
        {
            $"Selected {selectedCount} of {request.Candidates.Count} candidate load(s).",
        };

        if (request.Trailer.MaxWeightLbs is { } maxW && maxW > 0)
            parts.Add($"Weight {usedWeight:N0}/{maxW:N0} lb.");
        else
            parts.Add("Weight capacity not enforced (trailer max weight unknown).");

        if (request.Trailer.MaxPallets is { } maxP && maxP > 0)
            parts.Add($"Pallets {usedPallets}/{maxP}.");
        else
            parts.Add("Pallet capacity not enforced (trailer max pallets unknown).");

        parts.Add(anyEstimated
            ? "Some leg miles are estimated (no Alvys mileage / coordinates) — cost is approximate."
            : "All leg miles are Alvys-derived.");

        return string.Join(" ", parts);
    }

    private CapacityCostResult Unsolved(string rationale) =>
        new(Solved: false, Plan: null, rationale, clock.GetUtcNow());

    private static Duration ToDuration(double seconds)
    {
        if (seconds <= 0) seconds = 1;
        var whole = (long)Math.Floor(seconds);
        var nanos = (int)Math.Round((seconds - whole) * 1_000_000_000);
        return new Duration { Seconds = whole, Nanos = nanos };
    }
}
