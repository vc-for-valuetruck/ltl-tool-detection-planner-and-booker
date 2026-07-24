using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// Read-only reporting surface over the normalized accessorial/assignment history tables (see
/// <see cref="AccessorialRecord"/>, <see cref="LoadAssignmentRecord"/>). Additive to the LTL API —
/// no existing route/handler changes. Serves both a JSON listing (for in-tool use) and an RFC 4180
/// CSV export (for external reporting tools, e.g. Power BI's Text/CSV connector), matching the
/// existing margin-rollup export pattern. Every value returned here is either a normalized Alvys
/// read captured earlier or an honest empty/null — nothing is written to Alvys, and nothing here
/// feeds back into any live decision-support path.
/// </summary>
[ApiController]
[Route("api/ltl/reporting")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class ReportingHistoryController(
    IAccessorialStore accessorials, ILoadAssignmentStore assignments) : ControllerBase
{
    /// <summary>Recent accessorial history, most-recently-seen first, optionally filtered by load and/or entity type.</summary>
    [HttpGet("accessorials")]
    [ProducesResponseType(typeof(IReadOnlyList<AccessorialRecord>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AccessorialRecord>> GetAccessorials(
        [FromQuery] string? loadId, [FromQuery] AccessorialEntityType? entityType, [FromQuery] int max = 200)
        => Ok(accessorials.List(loadId, entityType, Math.Clamp(max <= 0 ? 200 : max, 1, 5000)));

    /// <summary>CSV export of the accessorial history for external reporting tools (e.g. Power BI).</summary>
    [HttpGet("accessorials/export")]
    [Produces("text/csv")]
    public ActionResult GetAccessorialsExport(
        [FromQuery] string? loadId, [FromQuery] AccessorialEntityType? entityType, [FromQuery] int max = 5000)
    {
        var rows = accessorials.List(loadId, entityType, Math.Clamp(max <= 0 ? 5000 : max, 1, 20000));
        var csv = AccessorialHistoryCsvWriter.Write(rows);
        var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv", "accessorial-history.csv");
    }

    /// <summary>Recent assignment history, newest first, optionally filtered to one load.</summary>
    [HttpGet("assignments-history")]
    [ProducesResponseType(typeof(IReadOnlyList<LoadAssignmentRecord>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LoadAssignmentRecord>> GetAssignments(
        [FromQuery] string? loadId, [FromQuery] int max = 200)
    {
        var limit = Math.Clamp(max <= 0 ? 200 : max, 1, 5000);
        var rows = string.IsNullOrWhiteSpace(loadId)
            ? assignments.ListRecent(limit)
            : assignments.ListForLoad(loadId, limit);
        return Ok(rows);
    }

    /// <summary>CSV export of the assignment history for external reporting tools (e.g. Power BI).</summary>
    [HttpGet("assignments-history/export")]
    [Produces("text/csv")]
    public ActionResult GetAssignmentsExport([FromQuery] string? loadId, [FromQuery] int max = 5000)
    {
        var limit = Math.Clamp(max <= 0 ? 5000 : max, 1, 20000);
        var rows = string.IsNullOrWhiteSpace(loadId)
            ? assignments.ListRecent(limit)
            : assignments.ListForLoad(loadId, limit);
        var csv = LoadAssignmentHistoryCsvWriter.Write(rows);
        var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv", "assignment-history.csv");
    }
}
