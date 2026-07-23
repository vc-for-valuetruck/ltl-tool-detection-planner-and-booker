using System.Security.Claims;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Direct-construction coverage for the Phase 4 consolidation endpoints that never touch
/// Alvys: lane-template CRUD (internal data), the combined-RPM billing view (echoed from an
/// audit, never re-derived), and the fire-and-forget click-card-copied metric. Uses in-memory
/// store doubles and a claims principal so <c>CurrentUser()</c> resolves.
/// </summary>
public sealed class ConsolidationControllerPhase4Tests
{
    private static ConsolidationController BuildController(
        ILaneTemplateStore laneTemplates,
        IConsolidationAuditStore audits)
    {
        var controller = new ConsolidationController(
            candidates: null!,
            plans: null!,
            audits: audits,
            laneTemplates: laneTemplates,
            options: Microsoft.Extensions.Options.Options.Create(new ConsolidationOptions()),
            corridorHealth: null!,
            autoExecute: null!,
            logger: NullLogger<ConsolidationController>.Instance);

        var identity = new ClaimsIdentity(
            [new Claim("preferred_username", "dispatch@valuetruck.com")], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    [Fact]
    public void SaveLaneTemplate_persists_and_returns_the_view()
    {
        var store = new FakeLaneTemplateStore();
        var controller = BuildController(store, new FakeAuditStore());

        var result = controller.SaveLaneTemplate(new SaveLaneTemplateRequest
        {
            Name = "  Verdef weekly  ",
            CorridorCode = "LAREDO_TO_DALLAS",
            CustomerName = " Verdef ",
            CadenceDays = 7,
        });

        var view = Assert.IsType<LaneTemplateView>(((OkObjectResult)result.Result!).Value);
        Assert.Equal("Verdef weekly", view.Name); // trimmed
        Assert.Equal("LAREDO_TO_DALLAS", view.CorridorCode);
        Assert.Equal("Verdef", view.CustomerName);
        Assert.Equal(7, view.CadenceDays);
        Assert.Equal("dispatch@valuetruck.com", view.CreatedBy);
        Assert.StartsWith("lane-", view.Id);
        Assert.Single(store.Items);
    }

    [Fact]
    public void SaveLaneTemplate_rejects_missing_name_and_corridor()
    {
        var controller = BuildController(new FakeLaneTemplateStore(), new FakeAuditStore());

        Assert.IsType<BadRequestObjectResult>(
            controller.SaveLaneTemplate(new SaveLaneTemplateRequest { Name = "", CorridorCode = "X" }).Result);
        Assert.IsType<BadRequestObjectResult>(
            controller.SaveLaneTemplate(new SaveLaneTemplateRequest { Name = "Ok", CorridorCode = " " }).Result);
    }

    [Fact]
    public void GetLaneTemplates_returns_the_stored_views()
    {
        var store = new FakeLaneTemplateStore();
        store.Add(new LaneTemplateRecord
        {
            Id = "lane-1",
            Name = "t",
            CorridorCode = "LAREDO_TO_DALLAS",
            CreatedBy = "u",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var controller = BuildController(store, new FakeAuditStore());

        var result = controller.GetLaneTemplates(corridorCode: null, customerName: null);
        var views = Assert.IsAssignableFrom<IReadOnlyList<LaneTemplateView>>(
            ((OkObjectResult)result.Result!).Value);
        Assert.Single(views);
        Assert.Equal("lane-1", views[0].Id);
    }

    [Fact]
    public void DeleteLaneTemplate_reports_no_content_or_not_found()
    {
        var store = new FakeLaneTemplateStore();
        store.Add(new LaneTemplateRecord
        {
            Id = "lane-1", Name = "t", CorridorCode = "C", CreatedBy = "u",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        var controller = BuildController(store, new FakeAuditStore());

        Assert.IsType<NoContentResult>(controller.DeleteLaneTemplate("lane-1"));
        Assert.IsType<NotFoundResult>(controller.DeleteLaneTemplate("lane-1"));
    }

    [Fact]
    public void GetCombinedRpm_requires_a_load_id()
    {
        var controller = BuildController(new FakeLaneTemplateStore(), new FakeAuditStore());
        Assert.IsType<BadRequestObjectResult>(controller.GetCombinedRpm("").Result);
    }

    [Fact]
    public void GetCombinedRpm_returns_not_found_shape_when_no_audit_on_file()
    {
        var controller = BuildController(new FakeLaneTemplateStore(), new FakeAuditStore());

        var view = Assert.IsType<CombinedPlanBillingView>(
            ((OkObjectResult)controller.GetCombinedRpm("L-404").Result!).Value);
        Assert.False(view.Found);
    }

    [Fact]
    public void GetCombinedRpm_echoes_the_most_recent_audit_for_the_parent()
    {
        var record = new ConsolidationAuditRecord
        {
            Id = "audit-1",
            CorridorCode = "LAREDO_TO_DALLAS",
            ParentLoadId = "L-100234",
            ParentLoadNumber = "L-100234",
            SiblingLoadIds = ["L-100241"],
            SiblingLoadNumbers = ["L-100241"],
            CombinedRevenue = 8200m,
            DriverLoadedMiles = 1050m,
            CombinedDriverTripValue = 8100m,
            CombinedRevenuePerMile = 7.71m,
            Blockers = [],
            AlvysWriteback = "NotPerformed",
            RecordedBy = "dispatch@valuetruck.com",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        var controller = BuildController(
            new FakeLaneTemplateStore(), new FakeAuditStore(record));

        var view = Assert.IsType<CombinedPlanBillingView>(
            ((OkObjectResult)controller.GetCombinedRpm("L-100234").Result!).Value);

        Assert.True(view.Found);
        Assert.Equal("audit-1", view.AuditId);
        Assert.Equal(7.71m, view.CombinedRevenuePerMile); // driver math, echoed not re-derived
        Assert.Equal(1050m, view.DriverLoadedMiles);
        Assert.Equal(8100m, view.CombinedDriverTripValue);
    }

    [Fact]
    public void RecordClickCardCopied_is_a_no_content_signal()
    {
        var controller = BuildController(new FakeLaneTemplateStore(), new FakeAuditStore());
        var result = controller.RecordClickCardCopied(
            new ClickCardCopiedRequest { CorridorCode = "LAREDO_TO_DALLAS", SiblingCount = 2 });
        Assert.IsType<NoContentResult>(result);
    }

    private sealed class FakeLaneTemplateStore : ILaneTemplateStore
    {
        public List<LaneTemplateRecord> Items { get; } = [];

        public LaneTemplateRecord Add(LaneTemplateRecord record)
        {
            Items.Add(record);
            return record;
        }

        public LaneTemplateRecord? Get(string id) => Items.FirstOrDefault(r => r.Id == id);

        public IReadOnlyList<LaneTemplateRecord> Query(LaneTemplateQuery query) =>
            Items.OrderByDescending(r => r.CreatedAt).ToArray();

        public bool Delete(string id)
        {
            var found = Items.FirstOrDefault(r => r.Id == id);
            if (found is null) return false;
            Items.Remove(found);
            return true;
        }
    }

    private sealed class FakeAuditStore(ConsolidationAuditRecord? record = null) : IConsolidationAuditStore
    {
        public ConsolidationAuditRecord Record(ConsolidationPlanResponse plan, string recordedBy) =>
            throw new NotSupportedException();

        public ConsolidationAuditRecord RecordUndo(ConsolidationPlanResponse plan, string recordedBy) =>
            throw new NotSupportedException();

        public IReadOnlyList<ConsolidationAuditRecord> ForParent(string parentLoadIdOrNumber) =>
            record is not null && string.Equals(record.ParentLoadId, parentLoadIdOrNumber, StringComparison.OrdinalIgnoreCase)
                ? [record]
                : [];

        public IReadOnlyList<ConsolidationAuditRecord> All() => record is not null ? [record] : [];
    }
}
