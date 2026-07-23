using LtlTool.Api.Features.Ltl.Consolidation;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Pure-logic tests for the five-click consolidation sequence builder
/// (docs/AUTO_CONSOLIDATE_SPEC.md §2). No gate, no Alvys, no config — only the request→operation
/// shaping: the §2 order, the parent/child targeting, the idempotency keys, and the
/// dispatch-miles-only rule (the child zeroing carries no LTL/main-load/waypoint payload).
/// </summary>
public sealed class ConsolidationAutoExecuteSequenceTests
{
    private static ConsolidationAutoExecuteRequest RequestWith(params string[] siblingLoadIds)
    {
        var request = new ConsolidationAutoExecuteRequest
        {
            ParentTripId = "T-PARENT",
            ParentLoadNumber = "L-PARENT",
            ActingUserId = "dispatcher-1",
            Reason = "pilot consolidation",
        };
        var seq = 1;
        foreach (var id in siblingLoadIds)
        {
            request.Siblings.Add(new ConsolidationAutoExecuteSibling
            {
                LoadId = id,
                LoadNumber = id,
                ChildTripId = $"T-{id}",
                CompanyId = $"C-{id}",
                Sequence = seq++,
            });
        }
        return request;
    }

    [Fact]
    public void Single_sibling_produces_the_four_ordered_operations()
    {
        var ops = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2"));

        // 1 parent set-refs + 1 waypoint + 1 zero-child + 1 child set-refs.
        Assert.Equal(4, ops.Count);
        Assert.Equal(
            new[] { "set-trip-references", "add-extended-stop", "zero-child-dispatch-miles", "set-trip-references" },
            ops.Select(o => o.OperationCode).ToArray());
        // Order is 1..4, strictly increasing.
        Assert.Equal(new[] { 1, 2, 3, 4 }, ops.Select(o => o.Order).ToArray());
    }

    [Fact]
    public void Multiple_siblings_group_by_step_not_by_sibling()
    {
        var ops = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2", "L-3"));

        // 1 + N + N + N = 1 + 2 + 2 + 2 = 7.
        Assert.Equal(7, ops.Count);
        Assert.Equal(
            new[]
            {
                "set-trip-references",       // parent
                "add-extended-stop",         // sibling L-2
                "add-extended-stop",         // sibling L-3
                "zero-child-dispatch-miles", // child L-2
                "zero-child-dispatch-miles", // child L-3
                "set-trip-references",       // child L-2
                "set-trip-references",       // child L-3
            },
            ops.Select(o => o.OperationCode).ToArray());
    }

    [Fact]
    public void Parent_set_references_targets_the_parent_trip_with_ltl_and_main_load_id()
    {
        var op = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2"))[0];

        Assert.Equal("set-trip-references", op.OperationCode);
        Assert.Equal("T-PARENT", op.Request.TripId);
        Assert.True(op.Request.LtlReference);
        Assert.Equal("L-PARENT", op.Request.MainLoadId);
        Assert.Equal("dispatcher-1", op.Request.ActingUserId);
        Assert.Equal("T-PARENT:set-trip-references", op.Request.IdempotencyKey);
    }

    [Fact]
    public void Waypoint_lands_on_the_parent_trip_keyed_by_company_and_sequence()
    {
        var op = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2"))
            .Single(o => o.OperationCode == "add-extended-stop");

        Assert.Equal("T-PARENT", op.Request.TripId); // waypoint is added to the PARENT trip
        Assert.NotNull(op.Request.WaypointStop);
        Assert.Equal("C-L-2", op.Request.WaypointStop!.CompanyId);
        Assert.Equal(1, op.Request.WaypointStop.Sequence);
        Assert.Equal("T-PARENT:add-extended-stop:C-L-2:1", op.Request.IdempotencyKey);
    }

    [Fact]
    public void Zero_child_dispatch_miles_carries_no_reference_or_waypoint_payload()
    {
        // Spec §2 step 3 + §6: this operation ONLY zeroes dispatch (loaded) miles. It must not
        // smuggle LTL/main-load references or a waypoint, and customer mileage is never touched.
        var op = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2"))
            .Single(o => o.OperationCode == "zero-child-dispatch-miles");

        Assert.Equal("T-L-2", op.Request.TripId); // the CHILD trip
        Assert.Null(op.Request.LtlReference);
        Assert.Null(op.Request.MainLoadId);
        Assert.Null(op.Request.WaypointStop);
        Assert.Equal("T-L-2:zero-child-dispatch-miles", op.Request.IdempotencyKey);
    }

    [Fact]
    public void Child_set_references_targets_the_child_trip_with_parent_main_load_id()
    {
        var op = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2"))
            .Last();

        Assert.Equal("set-trip-references", op.OperationCode);
        Assert.Equal("T-L-2", op.Request.TripId); // the CHILD trip
        Assert.True(op.Request.LtlReference);
        Assert.Equal("L-PARENT", op.Request.MainLoadId); // parent's load number
        Assert.Equal("T-L-2:set-trip-references", op.Request.IdempotencyKey);
    }

    [Fact]
    public void Every_idempotency_key_is_unique()
    {
        var ops = ConsolidationAutoExecuteSequence.Build(RequestWith("L-2", "L-3"));
        var keys = ops.Select(o => o.Request.IdempotencyKey).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
