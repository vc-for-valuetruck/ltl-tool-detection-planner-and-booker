using System.Security.Claims;
using System.Text.Json;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Phase 2 M4 agent command surface. <c>GET</c> advertises the tool-style command catalog (always
/// available, so a future LLM function-calling layer can discover the schema even when dispatch is
/// off); <c>POST</c> dispatches one command through <see cref="IAgentCommandDispatcher"/>.
///
/// <para>
/// Every command is read-only against Alvys — the surface reuses the existing decision-support
/// services and writes nothing upstream. POST returns 404 when the feature flag is off, 404 for an
/// unknown command, and 400 for invalid arguments.
/// </para>
/// </summary>
[ApiController]
[Route("api/ltl/agent/commands")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class AgentCommandController(IAgentCommandDispatcher dispatcher) : ControllerBase
{
    /// <summary>
    /// Schema discovery: the tool-style catalog of every agent command with its parameters. Served
    /// regardless of whether dispatch is enabled; <see cref="AgentCommandCatalogResponse.Enabled"/>
    /// tells the caller whether POST will actually run.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AgentCommandCatalogResponse), StatusCodes.Status200OK)]
    public ActionResult<AgentCommandCatalogResponse> GetCatalog() =>
        Ok(new AgentCommandCatalogResponse
        {
            Enabled = dispatcher.IsEnabled,
            Commands = dispatcher.Catalog,
        });

    /// <summary>
    /// Dispatch one command by name. The body is the raw command arguments object (may be empty for
    /// all-optional commands). 404 when the feature is disabled or the command is unknown; 400 on
    /// invalid arguments.
    /// </summary>
    [HttpPost("{command}")]
    [ProducesResponseType(typeof(AgentCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentCommandResult>> Dispatch(
        string command,
        [FromBody] JsonElement args,
        CancellationToken ct)
    {
        if (!dispatcher.IsEnabled)
        {
            return NotFound(new { error = "Agent commands are disabled." });
        }

        try
        {
            var result = await dispatcher.DispatchAsync(command, args, CurrentUser(), ct);
            return Ok(result);
        }
        catch (UnknownAgentCommandException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (AgentCommandValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}

/// <summary>Response for the catalog discovery endpoint.</summary>
public sealed record AgentCommandCatalogResponse
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<AgentCommandSchema> Commands { get; init; }
}
