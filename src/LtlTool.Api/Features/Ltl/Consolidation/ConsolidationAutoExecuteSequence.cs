using LtlTool.Api.Features.Integrations.Alvys.Writeback;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// One resolved operation in the auto-execute sequence: which registry operation to run, a
/// dispatcher-facing label, the target description, the sibling it acts on (when per-sibling), and
/// the fully-built <see cref="AlvysOperationRequest"/> (idempotency key included) the recorder runs.
/// </summary>
public sealed class ConsolidationPlannedOperation
{
    public required int Order { get; init; }
    public required string OperationCode { get; init; }
    public required string Title { get; init; }
    public required string Target { get; init; }
    public string? SiblingLoadId { get; init; }
    public required AlvysOperationRequest Request { get; init; }
}

/// <summary>
/// Pure builder for the five-click consolidation sequence (<c>docs/AUTO_CONSOLIDATE_SPEC.md</c> §2).
/// It only shapes requests — it never validates the gate, never touches Alvys, and never reads
/// configuration. The order matches the spec's summary table:
///
/// <list type="number">
/// <item>Set parent trip references (<c>LTL=true</c> + <c>main_load_id</c>=parent load number).</item>
/// <item>Add an extended-stop Waypoint on the <b>parent</b> trip, once per sibling.</item>
/// <item>Zero each <b>child</b> trip's dispatch (loaded) mileage.</item>
/// <item>Set each <b>child</b> trip's references (<c>LTL=true</c> + <c>main_load_id</c>=parent).</item>
/// </list>
///
/// (Click #4 — the parent LTL boolean — is folded into step 1.) Idempotency keys are
/// <c>{tripId}:{operation}</c> (waypoints additionally keyed by CompanyId + sequence) so a retried
/// run de-duplicates against the outbox rather than double-writing.
/// </summary>
public static class ConsolidationAutoExecuteSequence
{
    public static IReadOnlyList<ConsolidationPlannedOperation> Build(ConsolidationAutoExecuteRequest request)
    {
        var ops = new List<ConsolidationPlannedOperation>();
        var parentTripId = request.ParentTripId;
        var mainLoadId = request.ParentLoadNumber;
        var actingUser = request.ActingUserId;
        var reason = request.Reason;
        var order = 0;

        // Step 1 — parent trip references: LTL boolean (click #4, folded in) + main_load_id.
        ops.Add(new ConsolidationPlannedOperation
        {
            Order = ++order,
            OperationCode = "set-trip-references",
            Title = "Set parent trip references (LTL + main load id)",
            Target = $"parent trip {parentTripId}",
            Request = new AlvysOperationRequest
            {
                TripId = parentTripId,
                ActingUserId = actingUser,
                LtlReference = true,
                MainLoadId = mainLoadId,
                Reason = reason,
                IdempotencyKey = $"{parentTripId}:set-trip-references",
            },
        });

        // Step 2 — one Waypoint / extended stop on the PARENT trip per sibling.
        foreach (var s in request.Siblings)
        {
            ops.Add(new ConsolidationPlannedOperation
            {
                Order = ++order,
                OperationCode = "add-extended-stop",
                Title = "Add extended stop (Waypoint) on parent trip",
                Target = $"parent trip {parentTripId} ← sibling {s.LoadNumber ?? s.LoadId}",
                SiblingLoadId = s.LoadId,
                Request = new AlvysOperationRequest
                {
                    TripId = parentTripId,
                    ActingUserId = actingUser,
                    WaypointStop = new AlvysWaypointStop
                    {
                        CompanyId = s.CompanyId,
                        Sequence = s.Sequence,
                        ScheduledAt = s.ScheduledAt,
                    },
                    Reason = reason,
                    IdempotencyKey = $"{parentTripId}:add-extended-stop:{s.CompanyId}:{s.Sequence}",
                },
            });
        }

        // Step 3 — zero each CHILD trip's dispatch (loaded) mileage (customer miles preserved).
        foreach (var s in request.Siblings)
        {
            ops.Add(new ConsolidationPlannedOperation
            {
                Order = ++order,
                OperationCode = "zero-child-dispatch-miles",
                Title = "Zero child dispatch miles",
                Target = $"child trip {s.ChildTripId}",
                SiblingLoadId = s.LoadId,
                Request = new AlvysOperationRequest
                {
                    TripId = s.ChildTripId,
                    ActingUserId = actingUser,
                    Reason = reason,
                    IdempotencyKey = $"{s.ChildTripId}:zero-child-dispatch-miles",
                },
            });
        }

        // Step 5 — set each CHILD trip's references (main_load_id=parent, LTL=true).
        foreach (var s in request.Siblings)
        {
            ops.Add(new ConsolidationPlannedOperation
            {
                Order = ++order,
                OperationCode = "set-trip-references",
                Title = "Set child trip references (LTL + main load id)",
                Target = $"child trip {s.ChildTripId}",
                SiblingLoadId = s.LoadId,
                Request = new AlvysOperationRequest
                {
                    TripId = s.ChildTripId,
                    ActingUserId = actingUser,
                    LtlReference = true,
                    MainLoadId = mainLoadId,
                    Reason = reason,
                    IdempotencyKey = $"{s.ChildTripId}:set-trip-references",
                },
            });
        }

        return ops;
    }
}
