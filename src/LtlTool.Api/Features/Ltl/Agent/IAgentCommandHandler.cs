using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// One agent command. Each handler owns exactly one command: it deserializes + validates its own args
/// DTO and calls the existing decision-support services — it never re-implements business logic.
/// Handlers are read-only against Alvys.
/// </summary>
public interface IAgentCommandHandler
{
    /// <summary>Catalog command name this handler serves (see <see cref="AgentCommandCatalog"/>).</summary>
    string Command { get; }

    /// <summary>
    /// Execute the command from raw JSON args. Throws <see cref="AgentCommandValidationException"/>
    /// on bad input (mapped to 400). Returns the response DTO placed in
    /// <see cref="AgentCommandResult.Data"/>.
    /// </summary>
    Task<object> HandleAsync(JsonElement args, CancellationToken ct);
}

/// <summary>Shared JSON settings + args deserialization for command handlers.</summary>
public static class AgentCommandJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Deserialize the command args into <typeparamref name="T"/>. An undefined/null element yields a
    /// fresh instance (commands with all-optional args tolerate an empty body). Malformed JSON is a
    /// validation error, not a 500.
    /// </summary>
    public static T Deserialize<T>(JsonElement args) where T : new()
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new T();
        }
        try
        {
            return args.Deserialize<T>(Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new AgentCommandValidationException($"Invalid arguments: {ex.Message}");
        }
    }
}
