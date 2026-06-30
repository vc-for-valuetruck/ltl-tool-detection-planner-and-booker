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
/// The catalogue of supported/known write operations. All five operations are
/// <see cref="AlvysLiveSupport.Supported"/> and will execute against the Alvys sandbox when
/// <see cref="AlvysWritebackMode.Sandbox"/> is fully configured. Sandbox execution is still
/// gated by configuration (recognised environment + sandbox base URL + credentials) so flipping
/// the mode alone can never reach a live/production tenant.
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
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "tender-accept",
            Kind = AlvysWriteOperationKind.TenderAccept,
            Title = "Accept tender",
            Description = "Accept an inbound EDI/tender offer that has been selected for booking.",
            WorkflowStage = "Match/Assign",
            RequiresEtag = true,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "trip-stop-arrival",
            Kind = AlvysWriteOperationKind.TripStopArrival,
            Title = "Record stop arrival",
            Description = "Record an arrival timestamp against a trip stop.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "trip-stop-departure",
            Kind = AlvysWriteOperationKind.TripStopDeparture,
            Title = "Record stop departure",
            Description = "Record a departure timestamp against a trip stop.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
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
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
    ];

    public static AlvysWriteOperationDescriptor? Find(string code) =>
        All.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
}
