using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// SignalR hub the dock UI subscribes to for real-time Yard→LTL events (load released, new
/// yard-originated opportunity). It is a one-way push hub — the server invokes client methods; clients
/// invoke nothing on the server. Guarded by the normal <c>AllowedEmailDomain</c> policy, so only
/// authenticated dispatchers receive the fan-out. The webhook receiver itself stays anonymous (machine
/// caller, HMAC-verified) and never touches this hub directly — the background processor does.
/// </summary>
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
public sealed class YardEventsHub : Hub
{
    /// <summary>The hub route the SPA connects to.</summary>
    public const string Path = "/hubs/yard-events";

    /// <summary>Client method invoked when a load is released at the yard.</summary>
    public const string LoadReleasedMethod = "loadReleased";

    /// <summary>Client method invoked when a new yard-originated LTL opportunity arrives.</summary>
    public const string OpportunityCreatedMethod = "opportunityCreated";
}
