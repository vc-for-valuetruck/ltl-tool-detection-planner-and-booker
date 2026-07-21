using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Notifications;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Notifications;

/// <summary>Tests the audit → T1 trigger mapping used by the background poller.</summary>
public sealed class NotificationTriggerEngineTests
{
    private static ConsolidationAuditRecord Record(
        string id = "audit-1",
        string parentId = "L-abc",
        string? parentNumber = "100234",
        string? customer = "Acme",
        params string[] siblingNumbers) => new()
    {
        Id = id,
        CorridorCode = "LAREDO_TO_DALLAS",
        ParentLoadId = parentId,
        ParentLoadNumber = parentNumber,
        ParentCustomerName = customer,
        SiblingLoadIds = siblingNumbers.Select((_, i) => $"s{i}").ToArray(),
        SiblingLoadNumbers = siblingNumbers,
        Blockers = [],
        RecordedBy = "dispatcher@x.com",
        RecordedAt = new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero),
    };

    [Fact]
    public void ToTrigger_uses_audit_id_as_source_key_for_dedupe()
    {
        var trigger = NotificationTriggerEngine.ToTrigger(Record(id: "audit-xyz"));

        Assert.Equal(NotificationStage.ConsolidationPlanCreated, trigger.Stage);
        Assert.Equal("audit-xyz", trigger.SourceKey);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero), trigger.OccurredAt);
    }

    [Fact]
    public void ToTrigger_links_to_load_number_when_present()
    {
        var trigger = NotificationTriggerEngine.ToTrigger(Record(parentNumber: "100234"));

        Assert.Equal("/ltl/loads/100234", trigger.LinkPath);
        Assert.Contains("100234", trigger.Title);
    }

    [Fact]
    public void ToTrigger_falls_back_to_load_id_when_number_missing()
    {
        var trigger = NotificationTriggerEngine.ToTrigger(Record(parentId: "L-abc", parentNumber: null));

        Assert.Equal("/ltl/loads/L-abc", trigger.LinkPath);
    }

    [Fact]
    public void ToTrigger_summary_names_customer_and_sibling_count()
    {
        var trigger = NotificationTriggerEngine.ToTrigger(
            Record(customer: "Acme", siblingNumbers: ["200", "201"]));

        Assert.Contains("Acme", trigger.Summary);
        Assert.Contains("2 sibling", trigger.Summary);
    }

    private static readonly DateTimeOffset Eta = new(2026, 7, 20, 14, 0, 0, TimeSpan.Zero);

    private static LtlLoadSummary PredictedLateLoad(
        string id = "L-late",
        string? loadNumber = "100500",
        DateTimeOffset? window = null) => new()
    {
        Id = id,
        LoadNumber = loadNumber,
        Status = "In Transit",
        PredictedDeliveryAt = Eta,
        PredictedLate = true,
        ScheduledDeliveryAt = window,
        EtaBasis = "Derived from PCMiler loaded miles (470 mi) via Alvys ÷ 47 mph average.",
    };

    [Fact]
    public void ToPredictedLateTrigger_uses_load_id_as_source_key_and_eta_as_occurred_at()
    {
        // OccurredAt = predicted ETA (not "now") so re-polling an unchanged prediction dedupes.
        var trigger = NotificationTriggerEngine.ToPredictedLateTrigger(PredictedLateLoad());

        Assert.Equal(NotificationStage.ExceptionRaised, trigger.Stage);
        Assert.Equal("L-late", trigger.SourceKey);
        Assert.Equal("L-late", trigger.LoadId);
        Assert.Equal(Eta, trigger.OccurredAt);
    }

    [Fact]
    public void ToPredictedLateTrigger_links_to_load_number_when_present()
    {
        var trigger = NotificationTriggerEngine.ToPredictedLateTrigger(PredictedLateLoad(loadNumber: "100500"));

        Assert.Equal("/ltl/loads/100500", trigger.LinkPath);
        Assert.Contains("100500", trigger.Title);
    }

    [Fact]
    public void ToPredictedLateTrigger_falls_back_to_load_id_when_number_missing()
    {
        var trigger = NotificationTriggerEngine.ToPredictedLateTrigger(
            PredictedLateLoad(id: "L-late", loadNumber: null));

        Assert.Equal("/ltl/loads/L-late", trigger.LinkPath);
    }

    [Fact]
    public void ToPredictedLateTrigger_summary_names_window_and_basis_when_window_present()
    {
        var window = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var trigger = NotificationTriggerEngine.ToPredictedLateTrigger(PredictedLateLoad(window: window));

        Assert.Contains("delivery window", trigger.Summary);
        Assert.Contains("PCMiler", trigger.Summary);
    }

    private static readonly DateTimeOffset WindowEnd = new(2026, 7, 20, 22, 0, 0, TimeSpan.Zero);

    private static LtlLoadSummary LateDeliveryLoad(
        string id = "L-late-del",
        string? loadNumber = "1004253",
        string stopId = "stop-9",
        string? city = "Laredo",
        string? state = "TX") => new()
    {
        Id = id,
        LoadNumber = loadNumber,
        Status = "In Transit",
        LateDelivery = new LtlLateDelivery
        {
            StopId = stopId,
            DestinationCity = city,
            DestinationState = state,
            WindowEnd = WindowEnd,
            WindowBasis = "delivery window",
            HoursOverdue = 1.9,
            Message = "Late delivery — delivery window ended Jul 20, 2026 5:00 PM UTC-05:00, "
                + "no arrival recorded (per Alvys stop status)",
        },
    };

    [Fact]
    public void ToLateDeliveryTrigger_dedupes_on_load_and_stop_and_window_end()
    {
        // SourceKey = {loadId}:{stopId} and OccurredAt = window end, so a still-late delivery seen
        // on every poll produces one stable idempotency key (no re-fire storm).
        var trigger = NotificationTriggerEngine.ToLateDeliveryTrigger(LateDeliveryLoad());

        Assert.Equal(NotificationStage.ExceptionRaised, trigger.Stage);
        Assert.Equal("L-late-del:stop-9", trigger.SourceKey);
        Assert.Equal("L-late-del", trigger.LoadId);
        Assert.Equal(WindowEnd, trigger.OccurredAt);
    }

    [Fact]
    public void ToLateDeliveryTrigger_links_to_load_number_and_names_hours_and_destination()
    {
        var trigger = NotificationTriggerEngine.ToLateDeliveryTrigger(LateDeliveryLoad(loadNumber: "1004253"));

        Assert.Equal("/ltl/loads/1004253", trigger.LinkPath);
        Assert.Contains("1004253", trigger.Title);
        Assert.Contains("1.9h overdue", trigger.Summary);
        Assert.Contains("Laredo, TX", trigger.Summary);
    }

    [Fact]
    public void ToLateDeliveryTrigger_falls_back_to_load_id_when_number_missing()
    {
        var trigger = NotificationTriggerEngine.ToLateDeliveryTrigger(
            LateDeliveryLoad(id: "L-late-del", loadNumber: null));

        Assert.Equal("/ltl/loads/L-late-del", trigger.LinkPath);
    }

    private static readonly DateTimeOffset ArrivedAt = new(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);

    private static LtlLoadSummary StuckStopLoad(
        string id = "L-stuck",
        string? loadNumber = "1003339",
        string stopId = "stop-9",
        string? city = "Williamston",
        string? state = "NC") => new()
    {
        Id = id,
        LoadNumber = loadNumber,
        Status = "In Transit",
        StuckStop = new LtlStuckStop
        {
            StopId = stopId,
            StopType = "Delivery",
            City = city,
            State = state,
            ArrivedAt = ArrivedAt,
            HoursSinceArrival = 164.8,
            Message = "Stuck at stop — Delivery in Williamston, NC arrived Jul 14, 2026 3:00 AM "
                + "UTC+00:00, no departure recorded after 164.8h. Per Alvys stop status — driver "
                + "may not have closed the stop",
        },
    };

    [Fact]
    public void ToStuckStopTrigger_dedupes_on_load_and_stop_and_condition()
    {
        // SourceKey = {loadId}:{stopId}:stuck and OccurredAt = arrival, so a still-stuck stop seen
        // on every poll produces one stable idempotency key distinct from the late-delivery key.
        var trigger = NotificationTriggerEngine.ToStuckStopTrigger(StuckStopLoad());

        Assert.Equal(NotificationStage.ExceptionRaised, trigger.Stage);
        Assert.Equal("L-stuck:stop-9:stuck", trigger.SourceKey);
        Assert.Equal("L-stuck", trigger.LoadId);
        Assert.Equal(ArrivedAt, trigger.OccurredAt);
    }

    [Fact]
    public void ToStuckStopTrigger_links_to_load_number_and_names_hours_place_and_caveat()
    {
        var trigger = NotificationTriggerEngine.ToStuckStopTrigger(StuckStopLoad(loadNumber: "1003339"));

        Assert.Equal("/ltl/loads/1003339", trigger.LinkPath);
        Assert.Contains("1003339", trigger.Title);
        Assert.Contains("164.8h", trigger.Summary);
        Assert.Contains("Williamston, NC", trigger.Summary);
        Assert.Contains("driver may not have closed the stop", trigger.Summary);
    }

    [Fact]
    public void ToStuckStopTrigger_falls_back_to_load_id_when_number_missing()
    {
        var trigger = NotificationTriggerEngine.ToStuckStopTrigger(
            StuckStopLoad(id: "L-stuck", loadNumber: null));

        Assert.Equal("/ltl/loads/L-stuck", trigger.LinkPath);
    }
}
