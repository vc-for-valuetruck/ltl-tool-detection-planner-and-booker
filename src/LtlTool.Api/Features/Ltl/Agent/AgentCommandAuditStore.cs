using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// One audit row per agent-command invocation. Captures the command, a SHA-256 hash of the raw args
/// (never the raw args themselves — they may contain load references and are legible enough as a hash
/// for correlation), the acting user, and the timestamp. Mirrors the in-memory posture of
/// <c>InMemoryConsolidationAuditStore</c>.
/// </summary>
public sealed record AgentCommandAuditRecord
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public required string ArgsHash { get; init; }
    public required bool Ok { get; init; }
    public required string ActingUser { get; init; }
    public required DateTimeOffset InvokedAt { get; init; }
}

/// <summary>Store for agent-command audit rows. Abstracted so a durable store can replace the default.</summary>
public interface IAgentCommandAuditStore
{
    /// <summary>Record an invocation and return the stored row (server-assigned id + timestamp).</summary>
    AgentCommandAuditRecord Record(string command, string rawArgs, bool ok, string actingUser);

    /// <summary>All rows, newest first.</summary>
    IReadOnlyList<AgentCommandAuditRecord> All();

    /// <summary>Deterministic SHA-256 (hex) of the raw args string, for correlation without storing payloads.</summary>
    static string HashArgs(string rawArgs)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawArgs ?? ""));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>Thread-safe in-memory <see cref="IAgentCommandAuditStore"/>.</summary>
public sealed class InMemoryAgentCommandAuditStore(TimeProvider clock) : IAgentCommandAuditStore
{
    private readonly TimeProvider _clock = clock;
    private readonly ConcurrentQueue<AgentCommandAuditRecord> _rows = new();

    public AgentCommandAuditRecord Record(string command, string rawArgs, bool ok, string actingUser)
    {
        var record = new AgentCommandAuditRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            Command = command,
            ArgsHash = IAgentCommandAuditStore.HashArgs(rawArgs),
            Ok = ok,
            ActingUser = actingUser,
            InvokedAt = _clock.GetUtcNow(),
        };
        _rows.Enqueue(record);
        return record;
    }

    public IReadOnlyList<AgentCommandAuditRecord> All() =>
        _rows.OrderByDescending(r => r.InvokedAt).ToArray();
}
