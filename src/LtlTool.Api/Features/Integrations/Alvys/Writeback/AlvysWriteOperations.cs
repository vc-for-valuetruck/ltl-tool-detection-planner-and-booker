namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// The write-oriented Alvys operations relevant to the Search → Match → Assign → Bill workflow.
/// Each is modelled here as a definition only — payload construction and dry-run preview are
/// always available, but <b>live execution</b> is gated separately (see
/// <see cref="AlvysWriteOperationDescriptor.LiveSupport"/>).
/// </summary>
public enum AlvysWriteOperationKind
{
    /// <summary>Append an assignment/billing audit note to a load.</summary>
    CreateLoadNote,
    /// <summary>Accept an inbound tender offer.</summary>
    TenderAccept,
    /// <summary>Record a trip stop arrival timestamp.</summary>
    TripStopArrival,
    /// <summary>Record a trip stop departure timestamp.</summary>
    TripStopDeparture,
    /// <summary>Update a scoped set of fields on a load (optimistic-concurrency / ETag gated).</summary>
    LoadUpdate,
}

/// <summary>
/// Whether an operation can be <b>executed live</b> against the Alvys sandbox in this phase.
/// </summary>
public enum AlvysLiveSupport
{
    /// <summary>A documented, safely-scoped mutating endpoint exists and execution is wired.</summary>
    Supported,
    /// <summary>
    /// No documented mutating endpoint is available in the repo/context. The operation is
    /// dry-run/simulation only; live execution returns an explicit unsupported response that
    /// documents what is required to enable it. We do not guess endpoints.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Static definition of a write-oriented Alvys operation: its stable code, the workflow stage it
/// serves, whether it needs an ETag for optimistic concurrency, and — critically — whether live
/// sandbox execution is supported yet and, if not, what is required to enable it.
/// </summary>
public sealed class AlvysWriteOperationDescriptor
{
    public required string Code { get; init; }
    public required AlvysWriteOperationKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>The Search → Match → Assign → Bill stage this operation supports.</summary>
    public required string WorkflowStage { get; init; }

    /// <summary>True when the operation mutates an existing record and needs an ETag to be safe.</summary>
    public bool RequiresEtag { get; init; }

    /// <summary>Whether live sandbox execution is supported yet.</summary>
    public required AlvysLiveSupport LiveSupport { get; init; }

    /// <summary>
    /// When <see cref="LiveSupport"/> is <see cref="AlvysLiveSupport.Unsupported"/>, the specific
    /// information required to enable live execution (e.g. the documented endpoint + verb + body).
    /// </summary>
    public string? RequiredToEnable { get; init; }
}

/// <summary>
/// The catalogue of supported/known write operations. Live execution is currently
/// <see cref="AlvysLiveSupport.Unsupported"/> for every entry: the Alvys integration docs captured
/// in this repo cover read endpoints only, so we deliberately do not invent mutating routes. Each
/// descriptor records exactly what is needed to turn live sandbox execution on.
/// </summary>
public static class AlvysWriteOperationRegistry
{
    public static readonly IReadOnlyList<AlvysWriteOperationDescriptor> All =
    [
        new()
        {
            Code = "create-load-note",
            Kind = AlvysWriteOperationKind.CreateLoadNote,
            Title = "Create load note",
            Description =
                "Append an assignment/billing audit note to a load so the dispatch decision is " +
                "traceable in Alvys.",
            WorkflowStage = "Assign/Bill",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "A documented Alvys create-note endpoint (verb + path + request body) for " +
                "loads/{loadNumber}/notes. The repo currently documents the read-only GET notes " +
                "listing only; no POST note-creation contract is published.",
        },
        new()
        {
            Code = "tender-accept",
            Kind = AlvysWriteOperationKind.TenderAccept,
            Title = "Accept tender",
            Description = "Accept an inbound EDI/tender offer that has been selected for booking.",
            WorkflowStage = "Match/Assign",
            RequiresEtag = true,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "A documented Alvys tender-accept endpoint (verb + path + body) and its ETag/" +
                "concurrency contract. The repo documents tender search + get-by-id (read-only) " +
                "only; no accept/reject/cancel contract is published.",
        },
        new()
        {
            Code = "trip-stop-arrival",
            Kind = AlvysWriteOperationKind.TripStopArrival,
            Title = "Record stop arrival",
            Description = "Record an arrival timestamp against a trip stop.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "A documented Alvys trip-stop arrival endpoint (verb + path + body). The repo " +
                "documents the read-only GET trips/{tripId}/stops listing only.",
        },
        new()
        {
            Code = "trip-stop-departure",
            Kind = AlvysWriteOperationKind.TripStopDeparture,
            Title = "Record stop departure",
            Description = "Record a departure timestamp against a trip stop.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "A documented Alvys trip-stop departure endpoint (verb + path + body). The repo " +
                "documents the read-only GET trips/{tripId}/stops listing only.",
        },
        new()
        {
            Code = "load-update",
            Kind = AlvysWriteOperationKind.LoadUpdate,
            Title = "Update load fields",
            Description =
                "Update a scoped set of editable load fields with optimistic-concurrency (ETag) " +
                "protection.",
            WorkflowStage = "Assign/Bill",
            RequiresEtag = true,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "A documented Alvys load-update endpoint (verb + path), the exact set of safely " +
                "editable fields, and the ETag/concurrency contract. The repo documents read-only " +
                "load search/detail only and the load read model carries no ETag.",
        },
    ];

    public static AlvysWriteOperationDescriptor? Find(string code) =>
        All.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
}
