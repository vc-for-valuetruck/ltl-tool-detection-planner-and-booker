using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// Versioned v1 ingestion surface for the Yard→LTL scheduler pipeline. Yard is a peer system: it
/// POSTs freight events over this HTTP contract, and the LTL tool persists them to its own SQL store
/// and derives the scheduler projection. Nothing here reads or writes Alvys, and there is no
/// cross-database read of Yard's store.
///
/// <para>The write endpoint is service-to-service (Yard managed identity, app role/scope). The read
/// endpoints back the scheduler consumer and an operator audit/replay view; they use the same
/// service-to-service policy so a scheduler service identity can poll them.</para>
/// </summary>
[ApiController]
[Route("api/v1/yard-events")]
[Authorize(Policy = AccessPolicies.YardEventIngest)]
[Produces("application/json")]
public sealed class YardEventsController(
    YardEventIngestionService ingestion,
    IYardEventStore store) : ControllerBase
{
    /// <summary>
    /// Ingests one Yard event. Idempotent on <c>eventId</c> + source record identity:
    /// a first delivery is persisted (<b>202</b>), a repeat delivery is acked without reprocessing
    /// (<b>200</b>), and a contract violation is rejected (<b>400</b>) with the list of errors.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(YardEventAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(YardEventAcceptedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(YardEventRejectedResponse), StatusCodes.Status400BadRequest)]
    public ActionResult Ingest([FromBody] YardEventEnvelope? envelope)
    {
        var outcome = ingestion.Ingest(envelope);

        return outcome.Status switch
        {
            YardIngestionStatus.Invalid =>
                BadRequest(new YardEventRejectedResponse(outcome.Errors)),

            YardIngestionStatus.Duplicate =>
                Ok(YardEventAcceptedResponse.From("duplicate", outcome)),

            _ => StatusCode(
                StatusCodes.Status202Accepted,
                YardEventAcceptedResponse.From("accepted", outcome)),
        };
    }

    /// <summary>The scheduler projection for one source record, or 404 when none exists yet.</summary>
    [HttpGet("schedule-input/{sourceSystem}/{sourceRecordType}/{sourceRecordId}")]
    [ProducesResponseType(typeof(YardScheduleInput), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<YardScheduleInput> GetScheduleInput(
        string sourceSystem, string sourceRecordType, string sourceRecordId)
    {
        var projection = store.GetProjection(sourceSystem, sourceRecordType, sourceRecordId);
        return projection is null ? NotFound() : Ok(projection);
    }

    /// <summary>
    /// The scheduler worklist: projections matching the filter, most-recently-updated first. This is
    /// the read the Smart Scheduler consumes to turn Yard freight into schedulable work.
    /// </summary>
    [HttpGet("schedule-input")]
    [ProducesResponseType(typeof(IReadOnlyList<YardScheduleInput>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<YardScheduleInput>> ListScheduleInput(
        [FromQuery] ScheduleReadiness? readiness,
        [FromQuery] ScheduleHoldState? holdState,
        [FromQuery] string? sourceRecordType,
        [FromQuery] string? yardLocationId,
        [FromQuery] bool? schedulableOnly,
        [FromQuery] int max = 200) =>
        Ok(store.QueryProjections(new YardScheduleInputQuery(
            readiness, holdState, sourceRecordType, yardLocationId, schedulableOnly, max)));

    /// <summary>Recent inbox events, newest first (operator audit view).</summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(IReadOnlyList<YardEventRecord>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<YardEventRecord>> ListEvents([FromQuery] int max = 100) =>
        Ok(store.ListEvents(max));

    /// <summary>All inbox events for one source record, oldest occurrence first (audit / replay view).</summary>
    [HttpGet("events/{sourceSystem}/{sourceRecordType}/{sourceRecordId}")]
    [ProducesResponseType(typeof(IReadOnlyList<YardEventRecord>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<YardEventRecord>> ListEventsForRecord(
        string sourceSystem, string sourceRecordType, string sourceRecordId) =>
        Ok(store.ListEventsForRecord(sourceSystem, sourceRecordType, sourceRecordId));

    /// <summary>
    /// Rebuilds the projection for one source record from its stored events without ingesting a new
    /// event (admin/repair). Returns the rebuilt projection, or 404 when the record has no
    /// freight-affecting events. Deterministic: the result equals what live ingestion produced.
    /// </summary>
    [HttpPost("schedule-input/{sourceSystem}/{sourceRecordType}/{sourceRecordId}/replay")]
    [ProducesResponseType(typeof(YardScheduleInput), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<YardScheduleInput> Replay(
        string sourceSystem, string sourceRecordType, string sourceRecordId)
    {
        var projection = store.ReplayRecord(sourceSystem, sourceRecordType, sourceRecordId);
        return projection is null ? NotFound() : Ok(projection);
    }
}

/// <summary>202/200 body: the accept/duplicate disposition plus the current projection (if any).</summary>
public sealed record YardEventAcceptedResponse(
    string Status,
    string Category,
    bool AffectsSchedulerInput,
    YardScheduleInput? Projection)
{
    public static YardEventAcceptedResponse From(string status, YardIngestionOutcome outcome) =>
        new(status, outcome.Category.ToString(),
            YardEventClassifier.AffectsSchedulerInput(outcome.Category), outcome.Projection);
}

/// <summary>400 body: the ordered list of contract-validation errors.</summary>
public sealed record YardEventRejectedResponse(IReadOnlyList<string> Errors);
