using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>report-incident</c> — records an operator-reported corridor incident in <see cref="IncidentStore"/>
/// and returns the updated corridor risk snapshot. Incidents are an internal planning signal only: they
/// are never written to or read from Alvys, and they feed only the reference quote surge factor. Acting-
/// user attribution for each invocation lives in <see cref="AgentCommandAuditStore"/>; the incident
/// itself is stamped with a generic agent-command source.
/// </summary>
public sealed class ReportIncidentHandler(IncidentStore incidents) : IAgentCommandHandler
{
    public string Command => AgentCommandCatalog.ReportIncident;

    public Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<ReportIncidentArgs>(args);
        Validate(request);

        var risk = incidents.Report(
            request.Origin,
            request.Destination,
            request.Severity,
            request.Note,
            reportedBy: "agent-command");

        return Task.FromResult<object>(risk);
    }

    private static void Validate(ReportIncidentArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Origin) || string.IsNullOrWhiteSpace(args.Destination))
        {
            throw new AgentCommandValidationException("origin and destination are required.");
        }
        if (args.Severity is < 1 or > 5)
        {
            throw new AgentCommandValidationException("severity must be between 1 and 5.");
        }
    }
}
