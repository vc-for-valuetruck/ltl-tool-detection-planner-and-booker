using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>Result of validating + ingesting one envelope. Maps directly to the HTTP status.</summary>
public sealed record YardIngestionOutcome(
    YardIngestionStatus Status,
    IReadOnlyList<string> Errors,
    YardEventCategory Category,
    YardScheduleInput? Projection)
{
    public static YardIngestionOutcome Invalid(IReadOnlyList<string> errors) =>
        new(YardIngestionStatus.Invalid, errors, YardEventCategory.Unknown, null);
}

public enum YardIngestionStatus
{
    /// <summary>Validation failed → HTTP 400. Nothing persisted.</summary>
    Invalid,

    /// <summary>Newly accepted and persisted → HTTP 202.</summary>
    Accepted,

    /// <summary>Already processed (idempotent duplicate) → HTTP 200.</summary>
    Duplicate,
}

/// <summary>
/// Validates a <see cref="YardEventEnvelope"/> against the v1 contract, classifies it, and hands it
/// to the durable store idempotently. All operational data stays internal — nothing here reads or
/// writes Alvys. Emits structured logs and counters for accept/duplicate/reject.
/// </summary>
public sealed class YardEventIngestionService(
    IYardEventStore store,
    IOptions<YardIngestionOptions> options,
    YardIngestionMetrics metrics,
    TimeProvider clock,
    ILogger<YardEventIngestionService> logger)
{
    private readonly YardIngestionOptions _options = options.Value;

    public YardIngestionOutcome Ingest(YardEventEnvelope? envelope)
    {
        if (envelope is null)
            return Reject(["A JSON envelope body is required."]);

        var errors = Validate(envelope);
        if (errors.Count > 0)
            return Reject(errors);

        var category = YardEventClassifier.Classify(envelope.EventType);
        var affects = YardEventClassifier.AffectsSchedulerInput(category);

        var eventId = envelope.EventId!.Value.ToString();
        var sourceSystem = envelope.SourceSystem!.Trim();
        var sourceRecordType = envelope.SourceRecordType!.Trim();
        var sourceRecordId = envelope.SourceRecordId!.Trim();

        var record = new YardEventRecord
        {
            DedupeKey = DedupeKey(eventId, sourceSystem, sourceRecordType, sourceRecordId),
            EventId = eventId,
            SchemaVersion = envelope.SchemaVersion!.Value,
            EventType = envelope.EventType!.Trim(),
            Category = category.ToString(),
            AffectsSchedulerInput = affects,
            OccurredAt = envelope.OccurredAt!.Value.ToUniversalTime(),
            SourceSystem = sourceSystem,
            SourceRecordType = sourceRecordType,
            SourceRecordId = sourceRecordId,
            YardLocationId = envelope.YardLocationId!.Trim(),
            CorrelationId = string.IsNullOrWhiteSpace(envelope.CorrelationId) ? null : envelope.CorrelationId.Trim(),
            PayloadJson = envelope.Payload!.Value.GetRawText(),
        };

        var result = store.Append(record, clock.GetUtcNow());

        if (result.Status == YardAppendStatus.Duplicate)
        {
            metrics.Duplicate(category);
            logger.LogInformation(
                "Yard event duplicate acked. eventId={EventId} source={SourceSystem}/{SourceRecordType}/{SourceRecordId} category={Category} correlationId={CorrelationId}",
                eventId, sourceSystem, sourceRecordType, sourceRecordId, category, record.CorrelationId);
            return new YardIngestionOutcome(YardIngestionStatus.Duplicate, [], category, result.Projection);
        }

        metrics.Accepted(category);
        if (affects)
        {
            logger.LogInformation(
                "Yard event accepted. eventId={EventId} source={SourceSystem}/{SourceRecordType}/{SourceRecordId} category={Category} readiness={Readiness} correlationId={CorrelationId}",
                eventId, sourceSystem, sourceRecordType, sourceRecordId, category,
                result.Projection?.Readiness, record.CorrelationId);
        }
        else
        {
            logger.LogInformation(
                "Yard event accepted (administrative — audited, not projected). eventId={EventId} eventType={EventType} category={Category} correlationId={CorrelationId}",
                eventId, record.EventType, category, record.CorrelationId);
        }

        return new YardIngestionOutcome(YardIngestionStatus.Accepted, [], category, result.Projection);
    }

    private List<string> Validate(YardEventEnvelope e)
    {
        var errors = new List<string>();

        if (e.EventId is null || e.EventId.Value == Guid.Empty)
            errors.Add("eventId is required and must be a non-empty UUID.");

        if (e.SchemaVersion is null)
            errors.Add("schemaVersion is required.");
        else if (e.SchemaVersion.Value != _options.SupportedSchemaVersion)
            errors.Add($"schemaVersion {e.SchemaVersion.Value} is not supported; expected {_options.SupportedSchemaVersion}.");

        if (string.IsNullOrWhiteSpace(e.EventType))
            errors.Add("eventType is required.");

        if (e.OccurredAt is null)
            errors.Add("occurredAt is required and must be an ISO-8601 UTC timestamp.");

        if (string.IsNullOrWhiteSpace(e.SourceSystem))
            errors.Add("sourceSystem is required.");
        else if (!string.IsNullOrWhiteSpace(_options.ExpectedSourceSystem) &&
                 !string.Equals(e.SourceSystem.Trim(), _options.ExpectedSourceSystem, StringComparison.OrdinalIgnoreCase))
            errors.Add($"sourceSystem '{e.SourceSystem.Trim()}' is not accepted by this endpoint; expected '{_options.ExpectedSourceSystem}'.");

        if (string.IsNullOrWhiteSpace(e.SourceRecordType))
            errors.Add("sourceRecordType is required.");
        if (string.IsNullOrWhiteSpace(e.SourceRecordId))
            errors.Add("sourceRecordId is required.");
        if (string.IsNullOrWhiteSpace(e.YardLocationId))
            errors.Add("yardLocationId is required.");

        if (e.Payload is null)
            errors.Add("payload is required and must be a JSON object.");
        else if (e.Payload.Value.ValueKind != JsonValueKind.Object)
            errors.Add("payload must be a JSON object.");

        return errors;
    }

    private YardIngestionOutcome Reject(IReadOnlyList<string> errors)
    {
        metrics.Rejected();
        logger.LogWarning("Yard event rejected by validation: {Errors}", string.Join("; ", errors));
        return YardIngestionOutcome.Invalid(errors);
    }

    internal static string DedupeKey(string eventId, string sourceSystem, string sourceRecordType, string sourceRecordId) =>
        $"{eventId}:{sourceSystem}:{sourceRecordType}:{sourceRecordId}";
}
