using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// A read-only background agent. Identifies itself and reports whether it is enabled, so the
/// heartbeat surface and DI wiring can reason about it uniformly.
/// </summary>
public interface IAgent
{
    /// <summary>Stable agent identity used as the heartbeat key (e.g. <c>opportunity-sweeper</c>).</summary>
    string AgentName { get; }

    /// <summary>Whether this agent is turned on via config. False agents record a single 'off' heartbeat and sweep nothing.</summary>
    bool Enabled { get; }
}

/// <summary>
/// Outcome of one agent sweep. Kept honest: a healthy sweep carries the swept count; a degraded
/// sweep (Alvys unavailable / unexpected error) carries the error type and NO swept count — never a
/// fabricated zero-as-success.
/// </summary>
public sealed record AgentSweepResult
{
    public required string Status { get; init; }
    public int? WindowSweptCount { get; init; }
    public string? LastErrorType { get; init; }

    public static AgentSweepResult Healthy(int sweptCount) => new()
    {
        Status = AgentHeartbeatStatus.Healthy,
        WindowSweptCount = sweptCount,
    };

    public static AgentSweepResult Degraded(string lastErrorType) => new()
    {
        Status = AgentHeartbeatStatus.Degraded,
        LastErrorType = lastErrorType,
    };
}

/// <summary>
/// Base class for the read-only sweeping agents. Owns the shared lifecycle: OFF agents record a
/// single honest 'off' heartbeat and do nothing; enabled agents sweep immediately and then on a
/// <see cref="PeriodicTimer"/> cadence. Every sweep runs in its own DI scope (the source services
/// and EF heartbeat store are scoped) and is fully guarded — a thrown Alvys/EF error becomes a
/// 'degraded' heartbeat, never a crashed host and never a fabricated notification.
///
/// <para>
/// Alvys posture: read-only. Concrete agents call existing <see cref="IAlvysClient"/> reads and the
/// existing decision-support services; none writes to Alvys.
/// </para>
/// </summary>
public abstract class AgentBackgroundService(
    IServiceProvider services,
    TimeProvider clock,
    ILogger logger) : BackgroundService, IAgent
{
    public abstract string AgentName { get; }
    public abstract bool Enabled { get; }

    /// <summary>Poll cadence for this agent. Floored to a sane minimum by the base loop.</summary>
    protected abstract TimeSpan Interval { get; }

    protected IServiceProvider Services => services;
    protected TimeProvider Clock => clock;

    /// <summary>
    /// Performs one sweep against the given per-sweep scope. Returns a healthy result (with swept
    /// count) or a degraded result. Throwing is also acceptable — the base loop converts it to a
    /// degraded heartbeat — but preferring an explicit <see cref="AgentSweepResult.Degraded"/> keeps
    /// the honest "Alvys returned null" path testable.
    /// </summary>
    protected abstract Task<AgentSweepResult> SweepAsync(IServiceProvider scope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            // Honest heartbeat surface: a disabled agent is 'off', not absent. Recorded once.
            await RecordSafelyAsync(AgentSweepResult.Degraded(string.Empty) with
            {
                Status = AgentHeartbeatStatus.Off,
                LastErrorType = null,
            }, stoppingToken);
            return;
        }

        var interval = Interval < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : Interval;

        await RunSweepCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSweepCycleAsync(stoppingToken);
        }
    }

    /// <summary>
    /// One full cycle: disabled → record 'off'; enabled → guarded sweep → record the resulting
    /// heartbeat. Returns the recorded result so unit tests can assert without the timer loop.
    /// </summary>
    public async Task<AgentSweepResult> RunSweepCycleAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            var off = AgentSweepResult.Degraded(string.Empty) with
            {
                Status = AgentHeartbeatStatus.Off,
                LastErrorType = null,
            };
            await RecordSafelyAsync(off, ct);
            return off;
        }

        AgentSweepResult result;
        using (var scope = services.CreateScope())
        {
            try
            {
                result = await SweepAsync(scope.ServiceProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Any unexpected error (Alvys transport, EF, mapping) degrades honestly — no notification.
                logger.LogWarning(ex, "Agent {Agent} sweep failed; recording degraded heartbeat.", AgentName);
                result = AgentSweepResult.Degraded(ex.GetType().Name);
            }
        }

        await RecordSafelyAsync(result, ct);
        return result;
    }

    private async Task RecordSafelyAsync(AgentSweepResult result, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentHeartbeatStore>();
            await store.RecordAsync(
                AgentName,
                clock.GetUtcNow(),
                result.Status,
                result.WindowSweptCount,
                result.LastErrorType,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A heartbeat-store outage (e.g. DB unreachable) must never take the host down.
            logger.LogWarning(ex, "Agent {Agent} could not persist heartbeat.", AgentName);
        }
    }
}
