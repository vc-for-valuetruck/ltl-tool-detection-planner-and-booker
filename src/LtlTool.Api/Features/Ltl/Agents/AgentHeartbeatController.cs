using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Read-only heartbeat surface for the background agents. Additive to the LTL API — no existing
/// route/handler changes. Backs the Notifications tab's "agents" status strip so ops can see, at a
/// glance, whether each sweeper is healthy, degraded, or off. Nothing here touches Alvys.
/// </summary>
[ApiController]
[Route("api/ltl/agents")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class AgentHeartbeatController(IAgentHeartbeatStore store) : ControllerBase
{
    /// <summary>Latest heartbeat for every agent, ordered by agent name.</summary>
    [HttpGet("heartbeat")]
    [ProducesResponseType(typeof(IReadOnlyList<AgentHeartbeat>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AgentHeartbeat>>> GetAll(CancellationToken ct) =>
        Ok(await store.LatestAllAsync(ct));

    /// <summary>Latest heartbeat for one agent. 404 when the agent has never run.</summary>
    [HttpGet("heartbeat/{agent}")]
    [ProducesResponseType(typeof(AgentHeartbeat), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentHeartbeat>> GetOne(string agent, CancellationToken ct)
    {
        var heartbeat = await store.LatestAsync(agent, ct);
        return heartbeat is null ? NotFound() : Ok(heartbeat);
    }
}
