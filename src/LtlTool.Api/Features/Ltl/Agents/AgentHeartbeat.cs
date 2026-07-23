using Microsoft.EntityFrameworkCore;
using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Durable liveness marker for a background agent. One row per agent name (upserted each run), so
/// the Notifications tab / an ops panel can show whether each sweeper is healthy, degraded, or off
/// without reading logs. This is internal Value Truck telemetry — never Alvys data, never written
/// back to Alvys.
/// </summary>
public sealed class AgentHeartbeat
{
    public Guid Id { get; set; }

    /// <summary>Stable agent identity (e.g. <c>opportunity-sweeper</c>). Unique.</summary>
    public required string AgentName { get; set; }

    /// <summary>When the agent last completed a run cycle (success, degraded, or off).</summary>
    public DateTimeOffset LastRunAt { get; set; }

    /// <summary>One of <see cref="AgentHeartbeatStatus"/>: healthy / degraded / off.</summary>
    public required string Status { get; set; }

    /// <summary>How many items the agent swept in its window. Null when off or degraded (never faked).</summary>
    public int? WindowSweptCount { get; set; }

    /// <summary>Exception type name when degraded (never a message/secret). Null otherwise.</summary>
    public string? LastErrorType { get; set; }
}

/// <summary>Honest, closed set of heartbeat states. Never coerced — an unavailable Alvys is 'degraded', not 'healthy'.</summary>
public static class AgentHeartbeatStatus
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Off = "off";
}

/// <summary>
/// Persistence for agent heartbeats. Upserts a single row per agent name so the latest state is
/// always one read away. EF-backed in production (see <see cref="EfAgentHeartbeatStore"/>);
/// tests supply a hand-written double.
/// </summary>
public interface IAgentHeartbeatStore
{
    Task RecordAsync(
        string agentName,
        DateTimeOffset lastRunAt,
        string status,
        int? windowSweptCount,
        string? lastErrorType,
        CancellationToken ct);

    /// <summary>Latest heartbeat for every agent, ordered by agent name.</summary>
    Task<IReadOnlyList<AgentHeartbeat>> LatestAllAsync(CancellationToken ct);

    /// <summary>Latest heartbeat for one agent, or null when the agent has never run.</summary>
    Task<AgentHeartbeat?> LatestAsync(string agentName, CancellationToken ct);
}

/// <summary>
/// EF Core <see cref="IAgentHeartbeatStore"/>. Upserts one row per <see cref="AgentHeartbeat.AgentName"/>
/// (unique index enforces the invariant). Scoped to the <see cref="AppDbContext"/> lifetime.
/// </summary>
public sealed class EfAgentHeartbeatStore(AppDbContext db) : IAgentHeartbeatStore
{
    public async Task RecordAsync(
        string agentName,
        DateTimeOffset lastRunAt,
        string status,
        int? windowSweptCount,
        string? lastErrorType,
        CancellationToken ct)
    {
        var existing = await db.AgentHeartbeats
            .FirstOrDefaultAsync(h => h.AgentName == agentName, ct);

        if (existing is null)
        {
            db.AgentHeartbeats.Add(new AgentHeartbeat
            {
                Id = Guid.NewGuid(),
                AgentName = agentName,
                LastRunAt = lastRunAt,
                Status = status,
                WindowSweptCount = windowSweptCount,
                LastErrorType = lastErrorType,
            });
        }
        else
        {
            existing.LastRunAt = lastRunAt;
            existing.Status = status;
            existing.WindowSweptCount = windowSweptCount;
            existing.LastErrorType = lastErrorType;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentHeartbeat>> LatestAllAsync(CancellationToken ct) =>
        await db.AgentHeartbeats
            .OrderBy(h => h.AgentName)
            .ToListAsync(ct);

    public async Task<AgentHeartbeat?> LatestAsync(string agentName, CancellationToken ct) =>
        await db.AgentHeartbeats
            .FirstOrDefaultAsync(h => h.AgentName == agentName, ct);
}
