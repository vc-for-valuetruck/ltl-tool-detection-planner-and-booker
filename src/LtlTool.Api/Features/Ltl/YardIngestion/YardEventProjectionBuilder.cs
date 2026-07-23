using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// Pure, deterministic fold from an immutable event log to a scheduler projection. Given <em>all</em>
/// accepted events for one source record, it produces the single <see cref="YardScheduleInput"/> the
/// scheduler consumes. Because the store rebuilds via this fold on every append (and on replay),
/// out-of-order delivery is handled for free: the events are sorted by <c>(OccurredAt, Sequence)</c>
/// and the fold applies last-writer-wins, so the result never depends on arrival order.
///
/// <para>No Alvys, no I/O, no clock — everything is derived from the stored events, so a rebuild is
/// bit-for-bit reproducible from the audit trail.</para>
/// </summary>
public static class YardEventProjectionBuilder
{
    /// <summary>
    /// Builds the projection from the record's events, or returns <c>null</c> when none of them are
    /// freight-affecting (administrative-only records never become scheduler input).
    /// </summary>
    public static YardScheduleInput? Build(IReadOnlyCollection<YardEventRecord> events)
    {
        var relevant = events
            .Where(e => e.AffectsSchedulerInput)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Sequence)
            .ToList();
        if (relevant.Count == 0)
            return null;

        var first = relevant[0];
        var last = relevant[^1];

        var projection = new YardScheduleInput
        {
            Id = ProjectionId(first.SourceSystem, first.SourceRecordType, first.SourceRecordId),
            SourceSystem = first.SourceSystem,
            SourceRecordType = first.SourceRecordType,
            SourceRecordId = first.SourceRecordId,
            YardLocationId = first.YardLocationId,
            SchedulerEligible = true,
            Readiness = ScheduleReadiness.Provisional.ToString(),
            HoldState = ScheduleHoldState.None.ToString(),
            RelatedRecordIdsJson = "[]",
            LatestOccurredAt = last.OccurredAt,
            LatestEventType = last.EventType,
            LatestEventId = last.EventId,
            EventCount = relevant.Count,
            CreatedAt = relevant.Min(e => e.ReceivedAt),
            UpdatedAt = relevant.Max(e => e.ReceivedAt),
        };

        var hold = ScheduleHoldState.None;

        foreach (var evt in relevant)
        {
            var category = ParseCategory(evt.Category);
            var payload = TryParse(evt.PayloadJson);

            // Later events overlay the yard location the record now sits at.
            if (!string.IsNullOrWhiteSpace(evt.YardLocationId))
                projection.YardLocationId = evt.YardLocationId;

            // Equipment / freight / routing / timing: last-writer-wins by order. Absent fields never
            // clear a previously-known value, and are never coerced when never seen.
            OverlayEquipment(projection, payload);
            OverlayFreight(projection, payload);
            OverlayRouting(projection, payload);
            OverlayTiming(projection, payload);

            switch (category)
            {
                case YardEventCategory.LoadComplete:
                case YardEventCategory.UnloadComplete:
                    projection.DockCompleted = true;
                    break;

                case YardEventCategory.Exception:
                    projection.HasOpenException = true;
                    break;

                case YardEventCategory.Hold:
                    hold = ScheduleHoldState.Held;
                    break;
                case YardEventCategory.Release:
                    hold = ScheduleHoldState.Released;
                    break;
                case YardEventCategory.Cancellation:
                    hold = ScheduleHoldState.Cancelled;
                    break;

                case YardEventCategory.Split:
                case YardEventCategory.Consolidation:
                    ApplyRelationship(projection, category, payload);
                    break;
            }
        }

        projection.HoldState = hold.ToString();
        // Security clearance is the standing effect of the latest hold-family event being a release.
        projection.SecurityCleared = hold == ScheduleHoldState.Released;

        var milestones = (projection.DockCompleted ? 1 : 0) + (projection.SecurityCleared ? 1 : 0);
        projection.Completeness = milestones / 2.0;

        var ready = projection.DockCompleted
                    && projection.SecurityCleared
                    && hold != ScheduleHoldState.Cancelled
                    && hold != ScheduleHoldState.Held;
        projection.Readiness = (ready ? ScheduleReadiness.Ready : ScheduleReadiness.Provisional).ToString();

        return projection;
    }

    public static string ProjectionId(string sourceSystem, string sourceRecordType, string sourceRecordId) =>
        $"{sourceSystem}:{sourceRecordType}:{sourceRecordId}";

    private static void OverlayEquipment(YardScheduleInput p, JsonElement? payload)
    {
        if (String(payload, "truckId") is { } truck) p.TruckId = truck;
        if (String(payload, "trailerId") is { } trailer) p.TrailerId = trailer;
        if (String(payload, "dockId") is { } dock) p.DockId = dock;
    }

    private static void OverlayFreight(YardScheduleInput p, JsonElement? payload)
    {
        if (Number(payload, "weightLbs") is { } weight) p.WeightLbs = weight;
        if (Number(payload, "lengthInches") is { } length) p.LengthInches = length;
        if (Number(payload, "widthInches") is { } width) p.WidthInches = width;
        if (Number(payload, "heightInches") is { } height) p.HeightInches = height;
        if (Integer(payload, "pieceCount") is { } pieces) p.PieceCount = pieces;
    }

    private static void OverlayRouting(YardScheduleInput p, JsonElement? payload)
    {
        if (String(payload, "originLocationId") is { } origin) p.OriginLocationId = origin;
        if (String(payload, "destinationLocationId") is { } dest) p.DestinationLocationId = dest;
    }

    private static void OverlayTiming(YardScheduleInput p, JsonElement? payload)
    {
        if (DateTime(payload, "appointmentAt") is { } appt)
            p.AppointmentAt = appt;
    }

    private static void ApplyRelationship(YardScheduleInput p, YardEventCategory category, JsonElement? payload)
    {
        p.RelationshipType = category.ToString();
        if (String(payload, "parentSourceRecordId") is { } parent)
            p.ParentSourceRecordId = parent;

        if (payload is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("relatedRecordIds", out var related) &&
            related.ValueKind == JsonValueKind.Array)
        {
            var ids = related.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            p.RelatedRecordIdsJson = JsonSerializer.Serialize(ids);
        }
    }

    private static YardEventCategory ParseCategory(string category) =>
        Enum.TryParse<YardEventCategory>(category, out var parsed) ? parsed : YardEventCategory.Unknown;

    private static JsonElement? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static double? Number(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var d))
        {
            return d;
        }
        return null;
    }

    private static int? Integer(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var i))
        {
            return i;
        }
        return null;
    }

    private static DateTimeOffset? DateTime(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out var dto))
        {
            return dto;
        }
        return null;
    }
}
