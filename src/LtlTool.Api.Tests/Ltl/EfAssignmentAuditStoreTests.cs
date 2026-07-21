using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Assignment;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the durable EF Core assignment-audit store against a real (file-backed SQLite) database:
/// records survive being read back through a brand-new <see cref="AppDbContext"/>/store instance
/// (proving persistence, not in-process memory), the typed <see cref="AssignmentReasonType"/> and
/// warnings JSON round-trip, and the cross-load history filters (user/day/reasonType) return only the
/// matching audits. Internal data — <see cref="AssignmentAudit.AlvysWriteback"/> stays "NotPerformed".
/// </summary>
public sealed class EfAssignmentAuditStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-assignment-audits-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfAssignmentAuditStoreTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private static AssignmentRequest Request(
        AssignmentReasonType? reason = null, string? detail = null) => new()
    {
        DriverId = "D1",
        TruckId = "T1",
        TrailerId = "TR1",
        MatchScore = 82,
        MatchLabel = "Good Match",
        Notes = "dispatcher note",
        ReasonType = reason,
        OverrideReason = detail,
    };

    [Fact]
    public void Records_survive_a_new_store_and_context_instance_with_reason_and_warnings()
    {
        using (var ctx = NewContext())
        {
            new EfAssignmentAuditStore(ctx).Record(
                "L100",
                Request(AssignmentReasonType.ServiceRecovery, "late load, best available"),
                "dispatcher@valuetruck.com",
                [new AssignmentIssue { Code = "EQUIP_MISMATCH", Message = "Trailer differs", Severity = AssignmentIssueSeverity.Warn }]);
        }

        using var readCtx = NewContext();
        var reloaded = new EfAssignmentAuditStore(readCtx).ForLoad("L100");

        Assert.Single(reloaded);
        Assert.Equal(AssignmentReasonType.ServiceRecovery, reloaded[0].ReasonType);
        Assert.Equal("late load, best available", reloaded[0].OverrideReason);
        Assert.Equal("NotPerformed", reloaded[0].AlvysWriteback);
        Assert.Single(reloaded[0].Warnings);
        Assert.Equal("EQUIP_MISMATCH", reloaded[0].Warnings[0].Code);
    }

    [Fact]
    public void Missing_reason_type_persists_as_unspecified()
    {
        using (var ctx = NewContext())
        {
            new EfAssignmentAuditStore(ctx).Record("L200", Request(), "dispatcher@valuetruck.com");
        }

        using var readCtx = NewContext();
        var reloaded = new EfAssignmentAuditStore(readCtx).ForLoad("L200");

        Assert.Single(reloaded);
        Assert.Equal(AssignmentReasonType.Unspecified, reloaded[0].ReasonType);
        Assert.Empty(reloaded[0].Warnings);
    }

    [Fact]
    public void Query_filters_by_user_and_reason_type()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAssignmentAuditStore(ctx);
            store.Record("L1", Request(AssignmentReasonType.CustomerRequest), "alice@valuetruck.com");
            store.Record("L2", Request(AssignmentReasonType.CostOptimization), "bob@valuetruck.com");
            store.Record("L3", Request(AssignmentReasonType.CustomerRequest), "bob@valuetruck.com");
        }

        using var readCtx = NewContext();
        var store2 = new EfAssignmentAuditStore(readCtx);

        Assert.Equal(2, store2.Query(new AssignmentAuditQuery(RecordedBy: "bob@valuetruck.com")).Count);
        Assert.Single(store2.Query(new AssignmentAuditQuery(RecordedBy: "ALICE@valuetruck.com")));
        Assert.Equal(2, store2.Query(new AssignmentAuditQuery(ReasonType: AssignmentReasonType.CustomerRequest)).Count);
        Assert.Single(store2.Query(new AssignmentAuditQuery(
            RecordedBy: "bob@valuetruck.com", ReasonType: AssignmentReasonType.CustomerRequest)));
    }

    [Fact]
    public void Query_orders_newest_first_and_honors_max()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAssignmentAuditStore(ctx);
            store.Record("L1", Request(AssignmentReasonType.Other), "u@valuetruck.com");
            Thread.Sleep(5);
            store.Record("L2", Request(AssignmentReasonType.Other), "u@valuetruck.com");
        }

        using var readCtx = NewContext();
        var store2 = new EfAssignmentAuditStore(readCtx);

        var all = store2.Query(new AssignmentAuditQuery(RecordedBy: "u@valuetruck.com"));
        Assert.Equal("L2", all[0].LoadId);

        var capped = store2.Query(new AssignmentAuditQuery(RecordedBy: "u@valuetruck.com", Max: 1));
        Assert.Single(capped);
        Assert.Equal("L2", capped[0].LoadId);
    }
}
