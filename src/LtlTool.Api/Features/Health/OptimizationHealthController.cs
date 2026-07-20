using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Health;

/// <summary>
/// Anonymous readiness probe for the Phase 2 optimization layer. Reports which optimization
/// feature flags are on, whether the trailer-fit sidecar is reachable, and the result of a
/// tiny in-memory capacity/cost solver self-test. It exposes <b>no</b> operational or Alvys
/// data — only flag booleans and synthetic self-test outcomes — so it is safe to serve without
/// authentication (mirroring <see cref="HealthController"/>). The UAT health workflow probes this
/// to prove the environment is running with optimization actually enabled, not just deployed.
/// </summary>
[ApiController]
[Route("api/health/optimization")]
public sealed class OptimizationHealthController(
    IOptions<LtlOptions> ltlOptions,
    ITrailerFitService trailerFit,
    ICapacityCostSolver solver,
    IServiceProvider services,
    TimeProvider clock) : ControllerBase
{
    private readonly OptimizationOptions _optimization = ltlOptions.Value.Optimization;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var trailerFitReport = await BuildTrailerFitReportAsync(ct).ConfigureAwait(false);
        var solverReport = await BuildSolverReportAsync(ct).ConfigureAwait(false);

        // "degraded" when a flag is on but its component did not pass its self-check. When a flag
        // is off there is nothing to be unhealthy about — the Null engine is expected.
        var trailerFitHealthy = !_optimization.TrailerFit.Enabled || trailerFitReport.Reachable == true;
        var solverHealthy = !_optimization.Solver.Enabled || solverReport.Passed == true;
        var status = trailerFitHealthy && solverHealthy ? "ok" : "degraded";

        return Ok(new
        {
            status,
            utc = clock.GetUtcNow(),
            flags = new
            {
                trailerFit = _optimization.TrailerFit.Enabled,
                solver = _optimization.Solver.Enabled,
                agentCommands = _optimization.AgentCommands.Enabled,
            },
            trailerFit = trailerFitReport,
            solver = solverReport,
        });
    }

    private async Task<TrailerFitReport> BuildTrailerFitReportAsync(CancellationToken ct)
    {
        if (!_optimization.TrailerFit.Enabled)
        {
            return new TrailerFitReport(false, null, "Trailer-fit flag off — Null engine registered.");
        }

        // The typed client is only registered when the flag is on. Resolve it defensively so a
        // half-wired configuration reports "unreachable" instead of throwing during a health probe.
        var client = services.GetService<ITrailerFitClient>();
        if (client is null)
        {
            return new TrailerFitReport(true, false, "Trailer-fit flag on but no HTTP client is wired.");
        }

        var reachable = await client.PingAsync(ct).ConfigureAwait(false);
        return new TrailerFitReport(
            true,
            reachable,
            reachable ? "Sidecar answered GET /health." : "Sidecar did not answer GET /health.");
    }

    private async Task<SolverReport> BuildSolverReportAsync(CancellationToken ct)
    {
        if (!_optimization.Solver.Enabled)
        {
            return new SolverReport(false, null, "Solver flag off — Null solver registered.");
        }

        // Two synthetic loads well within a standard 53' dry-van envelope. No Alvys data — pure
        // in-memory constants — so the self-test is deterministic and exposes nothing.
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [
                new CapacityCostCandidate("__smoke_a__", WeightLbs: 10_000m, Pallets: 8, Revenue: 1_500m, Miles: 100m, Mandatory: true),
                new CapacityCostCandidate("__smoke_b__", WeightLbs: 12_000m, Pallets: 9, Revenue: 1_400m, Miles: 120m),
            ]);

        try
        {
            var result = await solver.SolveAsync(request, ct).ConfigureAwait(false);
            return new SolverReport(
                true,
                result.Solved,
                result.Solved
                    ? "2-load in-memory solve returned a feasible plan."
                    : "2-load in-memory solve returned no plan.");
        }
        catch (Exception ex)
        {
            return new SolverReport(true, false, $"Solver self-test threw: {ex.GetType().Name}.");
        }
    }

    private sealed record TrailerFitReport(bool Enabled, bool? Reachable, string Detail);

    private sealed record SolverReport(bool Enabled, bool? Passed, string Detail);
}
