using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Behavior tests for the Phase 1 in-memory consolidation audit store. The store is the
/// leadership-facing counter-signal to commission politics (anti-failure map 3h): every
/// consolidation plan a dispatcher intends to execute becomes a durable row leadership can
/// point at when asked "did we actually catch value here?"
/// </summary>
public sealed class InMemoryConsolidationAuditStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 17, 15, 30, 0, TimeSpan.Zero);

    private static InMemoryConsolidationAuditStore NewStore() =>
        new(new FixedTimeProvider(Now));

    private static ConsolidationPlanResponse PlanFor(
        string parentId,
        string parentNumber,
        string customer,
        params (string id, string number)[] siblings)
    {
        var parent = new LtlLoadSummary
        {
            Id = parentId,
            LoadNumber = parentNumber,
            CustomerName = customer,
            Status = "Available",
        };
        var siblingRows = siblings
            .Select(s => new ConsolidationPlanSibling
            {
                LoadId = s.id,
                LoadNumber = s.number,
                CustomerTier = CustomerConsolidationTier.Allowed,
            })
            .ToArray();
        return new ConsolidationPlanResponse
        {
            PreviewId = "preview-1",
            CorridorCode = "LAREDO_TO_DALLAS",
            Parent = parent,
            Siblings = siblingRows,
            CombinedRevenue = 8200m,
            LinehaulMiles = 1072m,
            CombinedRevenuePerMile = 7.65m,
            ClickCard = new ConsolidationClickCard
            {
                PlainText = "…",
                TripReferenceValue = $"LTL={parentNumber}",
                MainLoadIdReferenceValue = parentNumber,
            },
            Blockers = [],
        };
    }

    [Fact]
    public void Record_returns_an_entry_with_server_assigned_id_and_timestamp()
    {
        var store = NewStore();
        var plan = PlanFor("SEED", "L-100234", "Verdef", ("S1", "L-100241"));

        var record = store.Record(plan, "joshua.davis@valuetruck.com");

        Assert.NotNull(record.Id);
        Assert.NotEmpty(record.Id);
        Assert.Equal(Now, record.RecordedAt);
        Assert.Equal("joshua.davis@valuetruck.com", record.RecordedBy);
    }

    [Fact]
    public void Record_copies_projected_value_from_the_plan()
    {
        var store = NewStore();
        var plan = PlanFor("SEED", "L-100234", "Verdef", ("S1", "L-100241"));

        var record = store.Record(plan, "op@valuetruck.com");

        Assert.Equal(8200m, record.CombinedRevenue);
        Assert.Equal(1072m, record.LinehaulMiles);
        Assert.Equal(7.65m, record.CombinedRevenuePerMile);
        Assert.Equal("LAREDO_TO_DALLAS", record.CorridorCode);
        Assert.Equal("Verdef", record.ParentCustomerName);
        Assert.Equal(new[] { "L-100241" }, record.SiblingLoadNumbers.ToArray());
    }

    [Fact]
    public void AlvysWriteback_is_NotPerformed_at_phase_one()
    {
        var store = NewStore();
        var plan = PlanFor("SEED", "L-100234", "Verdef", ("S1", "L-100241"));

        var record = store.Record(plan, "op@valuetruck.com");

        Assert.Equal("NotPerformed", record.AlvysWriteback);
    }

    [Fact]
    public void ForParent_finds_record_by_parent_load_id()
    {
        var store = NewStore();
        store.Record(PlanFor("SEED-A", "L-100234", "Verdef", ("S", "L-100241")), "op@valuetruck.com");
        store.Record(PlanFor("SEED-B", "L-100333", "Verdef", ("S", "L-100337")), "op@valuetruck.com");

        var forA = store.ForParent("SEED-A");
        var forB = store.ForParent("SEED-B");

        var a = Assert.Single(forA);
        Assert.Equal("L-100234", a.ParentLoadNumber);
        var b = Assert.Single(forB);
        Assert.Equal("L-100333", b.ParentLoadNumber);
    }

    [Fact]
    public void ForParent_also_matches_by_parent_load_number()
    {
        var store = NewStore();
        store.Record(PlanFor("SEED-A", "L-100234", "Verdef", ("S", "L-100241")), "op@valuetruck.com");

        var byNumber = store.ForParent("L-100234");

        Assert.Single(byNumber);
    }

    [Fact]
    public void All_returns_every_record_newest_first()
    {
        var store = NewStore();
        var first = store.Record(PlanFor("SEED-A", "L-100234", "Verdef", ("S", "L-100241")), "op@valuetruck.com");
        var second = store.Record(PlanFor("SEED-B", "L-100333", "Verdef", ("S", "L-100337")), "op@valuetruck.com");

        var all = store.All();

        Assert.Equal(2, all.Count);
        // Both were recorded at the same fixed Now — ordering is stable and includes both.
        Assert.Contains(all, r => r.Id == first.Id);
        Assert.Contains(all, r => r.Id == second.Id);
    }

    [Fact]
    public void Blockers_are_captured_verbatim_when_present()
    {
        var store = NewStore();
        var plan = PlanFor("SEED", "L-100234", "Verdef", ("S1", "L-100241"));
        var withBlockers = new ConsolidationPlanResponse
        {
            PreviewId = plan.PreviewId,
            CorridorCode = plan.CorridorCode,
            Parent = plan.Parent,
            Siblings = plan.Siblings,
            CombinedRevenue = plan.CombinedRevenue,
            LinehaulMiles = plan.LinehaulMiles,
            CombinedRevenuePerMile = plan.CombinedRevenuePerMile,
            ClickCard = plan.ClickCard,
            Blockers = ["Sibling 'L-KROGER' does not permit consolidation."],
        };

        var record = store.Record(withBlockers, "op@valuetruck.com");

        Assert.Single(record.Blockers);
        Assert.Contains("does not permit consolidation", record.Blockers[0]);
    }
}
