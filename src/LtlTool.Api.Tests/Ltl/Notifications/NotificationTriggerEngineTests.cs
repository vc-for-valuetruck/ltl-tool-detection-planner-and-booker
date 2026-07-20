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
}
