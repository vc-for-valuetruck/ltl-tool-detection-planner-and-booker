using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Routes an agent command name + raw JSON args to the owning <see cref="IAgentCommandHandler"/>,
/// audits every invocation, and wraps the handler result in an <see cref="AgentCommandResult"/>. The
/// catalog is always available (for schema discovery) even when dispatch is disabled.
/// </summary>
public interface IAgentCommandDispatcher
{
    /// <summary>Whether POST dispatch is enabled (feature flag). Catalog discovery works regardless.</summary>
    bool IsEnabled { get; }

    /// <summary>The tool-style command catalog for schema discovery.</summary>
    IReadOnlyList<AgentCommandSchema> Catalog { get; }

    /// <summary>
    /// Dispatch one command. Throws <see cref="UnknownAgentCommandException"/> (→ 404) for an unknown
    /// command and <see cref="AgentCommandValidationException"/> (→ 400) for bad args. Successful and
    /// validation-failed invocations are both audited.
    /// </summary>
    Task<AgentCommandResult> DispatchAsync(string command, JsonElement args, string actingUser, CancellationToken ct);
}

/// <summary>Real dispatcher — registered only when the agent-command feature flag is on.</summary>
public sealed class AgentCommandDispatcher : IAgentCommandDispatcher
{
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _handlers;
    private readonly IAgentCommandAuditStore _audits;
    private readonly TimeProvider _clock;

    public AgentCommandDispatcher(
        IEnumerable<IAgentCommandHandler> handlers,
        IAgentCommandAuditStore audits,
        TimeProvider clock)
    {
        _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
        _audits = audits;
        _clock = clock;
    }

    public bool IsEnabled => true;

    public IReadOnlyList<AgentCommandSchema> Catalog => AgentCommandCatalog.All;

    public async Task<AgentCommandResult> DispatchAsync(
        string command, JsonElement args, string actingUser, CancellationToken ct)
    {
        var schema = AgentCommandCatalog.Get(command)
            ?? throw new UnknownAgentCommandException(command);
        if (!_handlers.TryGetValue(schema.Name, out var handler))
        {
            throw new UnknownAgentCommandException(command);
        }

        var rawArgs = args.ValueKind is JsonValueKind.Undefined ? "" : args.GetRawText();
        try
        {
            var data = await handler.HandleAsync(args, ct);
            _audits.Record(schema.Name, rawArgs, ok: true, actingUser);
            return new AgentCommandResult
            {
                Ok = true,
                Command = schema.Name,
                Data = data,
                ExecutedAt = _clock.GetUtcNow(),
            };
        }
        catch (AgentCommandValidationException)
        {
            _audits.Record(schema.Name, rawArgs, ok: false, actingUser);
            throw;
        }
    }
}

/// <summary>
/// Disabled-feature fallback. Still advertises the catalog (so the GET discovery endpoint and a future
/// LLM layer can inspect the surface) but refuses to dispatch — the controller checks
/// <see cref="IsEnabled"/> and returns 404 before ever calling <see cref="DispatchAsync"/>.
/// </summary>
public sealed class NullAgentCommandDispatcher : IAgentCommandDispatcher
{
    public bool IsEnabled => false;

    public IReadOnlyList<AgentCommandSchema> Catalog => AgentCommandCatalog.All;

    public Task<AgentCommandResult> DispatchAsync(
        string command, JsonElement args, string actingUser, CancellationToken ct) =>
        throw new InvalidOperationException("Agent commands are disabled.");
}
