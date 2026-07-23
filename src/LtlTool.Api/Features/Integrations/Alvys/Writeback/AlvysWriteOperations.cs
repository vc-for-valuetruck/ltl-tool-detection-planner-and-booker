namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// The Alvys-documented values for the <c>NoteType</c> field on create-note requests.
/// Only these four values are accepted; any other value will be rejected by the API.
/// </summary>
public static class AlvysNoteTypes
{
    public const string System = "System";
    public const string General = "General";
    public const string Assignment = "Assignment";
    public const string Safety = "Safety";

    public static readonly IReadOnlyList<string> All = [System, General, Assignment, Safety];

    public static bool IsValid(string? value) =>
        All.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The Alvys-documented <c>DocumentType</c> values accepted by the load-document upload endpoint
/// (<c>POST /api/p/v{version}/loads/{loadNumber}/document</c>). Only these values are accepted;
/// any other value is rejected before a live upload is built, so a caller can never push a
/// document type Alvys does not recognise. Comparison is case-insensitive but the canonical
/// spelling is sent to Alvys.
/// </summary>
public static class AlvysLoadDocumentTypes
{
    public static readonly IReadOnlyList<string> All =
    [
        "Customer Rate and Load Confirmation",
        "Customer Load Confirmation",
        "Customer Rate Confirmation",
        "Signed Customer Rate Confirmation",
        "Proof of Delivery",
        "Proof of Pickup",
        "Bill of Lading",
        "Shipping Labels",
    ];

    public static bool IsValid(string? value) =>
        All.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the canonical spelling for a case-insensitive match, or null when unknown.</summary>
    public static string? Canonical(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Alvys-documented <c>DocumentType</c> values accepted by the trip-document upload endpoint
/// (<c>POST /api/p/v{version}/trips/{tripId}/document</c>). Broader than the load list (adds
/// carrier/manifest/scale-ticket types). Same safety boundary applies.
/// </summary>
public static class AlvysTripDocumentTypes
{
    public static readonly IReadOnlyList<string> All =
    [
        "Proof of Delivery",
        "Bill of Lading",
        "Carrier Rate Confirmation",
        "Load Manifest",
        "Trip Report",
        "Temp. Log",
        "Proof of Pickup",
        "Scale Ticket",
        "Notice of Assignment",
        "NOA",
        "Shipping Labels",
    ];

    public static bool IsValid(string? value) =>
        All.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string? Canonical(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// MIME + size constraints for Alvys document uploads. The load endpoint accepts PDF/JPEG/PNG up to
/// 10 MB (the docs cite 25 MB in places; we enforce the conservative 10 MB). The trip endpoint also
/// accepts GIF and permits up to 25 MB. Enforced server-side so an over-large or wrong-type file is
/// rejected before any bytes reach Alvys.
/// </summary>
public static class AlvysDocumentUploadLimits
{
    public const long LoadMaxBytes = 10L * 1024 * 1024;
    public const long TripMaxBytes = 25L * 1024 * 1024;

    public static readonly IReadOnlyList<string> LoadContentTypes =
        ["application/pdf", "image/jpeg", "image/png"];

    public static readonly IReadOnlyList<string> TripContentTypes =
        ["application/pdf", "image/jpeg", "image/png", "image/gif"];

    public static bool IsAllowedContentType(IReadOnlyList<string> allowed, string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && allowed.Contains(contentType.Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The allowlist of load fields writable via the Alvys load-update (PATCH) endpoint. Today only
/// <c>OrderNumber</c> is writable (≤30 chars). The list is the safety boundary: any other field
/// is rejected before a live PATCH is built, so an over-broad caller can never mutate fields Alvys
/// does not expose for write.
/// </summary>
public static class AlvysLoadUpdateFields
{
    public const string OrderNumber = "OrderNumber";
    public const int OrderNumberMaxLength = 30;

    public static readonly IReadOnlyList<string> Writable = [OrderNumber];

    public static bool IsWritable(string? field) =>
        Writable.Contains(field, StringComparer.OrdinalIgnoreCase);

    public static bool IsOrderNumber(string? field) =>
        string.Equals(field, OrderNumber, StringComparison.OrdinalIgnoreCase);
}

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
    /// <summary>Assign a carrier and/or assets (driver, truck, trailer) to a trip.</summary>
    TripAssign,
    /// <summary>Dispatch a trip that already has a carrier and assets assigned.</summary>
    TripDispatch,
    /// <summary>Update a carrier's operational status (optimistic-concurrency / ETag gated).</summary>
    CarrierStatusUpdate,

    // --- Billing document writes (Public API — contracted 2026-07-21; decision #11) --------------

    /// <summary>Upload a single billing document (POD/BOL/rate-con/etc.) to a load (Public API).</summary>
    UploadLoadDocument,
    /// <summary>Upload a single billing document to a trip (Public API).</summary>
    UploadTripDocument,
    /// <summary>Attach a carrier invoice document to a trip (Public API, separately flag-gated).</summary>
    CreateCarrierInvoice,
    /// <summary>Post a customer payment against an invoiced load (Public API).</summary>
    CreateCustomerPayment,

    // --- Phase-2 consolidation writes (internal API — observed, not contracted; decision #10) ---

    /// <summary>Create a Waypoint / extended stop on the parent trip (internal API).</summary>
    AddExtendedStop,
    /// <summary>Zero a child trip's dispatch (loaded) mileage, preserving customer miles (internal API).</summary>
    ZeroChildDispatchMiles,
    /// <summary>Set trip references — the <c>LTL</c> boolean and <c>main_load_id</c> string (internal API).</summary>
    SetTripReferences,
}

/// <summary>
/// Which Alvys API surface an operation is dispatched against. The two surfaces authenticate
/// differently and have different safety postures (decision #10 in
/// <c>docs/ALVYS_API_DECISIONS.md</c>).
/// </summary>
public enum AlvysWriteApiSurface
{
    /// <summary>
    /// The Alvys <b>Public</b> API — client-credentials (machine-to-machine) auth. Routes/verbs are
    /// contracted in the Alvys API docs. All pre-Phase-2 write operations use this surface.
    /// </summary>
    Public,

    /// <summary>
    /// The Alvys <b>internal</b> API (the endpoints the Alvys web app calls) — per-acting-user Auth0
    /// session-token auth. Endpoints are <b>observed, not contracted</b> and can change without
    /// notice, so every internal call site requires a snapshot regression test. Phase-2
    /// consolidation writes use this surface.
    /// </summary>
    Internal,
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

    /// <summary>
    /// Which Alvys API surface this operation dispatches against. Defaults to
    /// <see cref="AlvysWriteApiSurface.Public"/>; Phase-2 consolidation writes set
    /// <see cref="AlvysWriteApiSurface.Internal"/>. The readiness surface only reports Public
    /// operations; internal operations are gated by <see cref="AlvysInternalApiOptions"/>.
    /// </summary>
    public AlvysWriteApiSurface Surface { get; init; } = AlvysWriteApiSurface.Public;

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
                "Update editable load fields with optimistic-concurrency (ETag) protection. " +
                "Currently only OrderNumber (≤30 chars) is writable via this endpoint.",
            WorkflowStage = "Assign/Bill",
            RequiresEtag = true,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "trip-assign",
            Kind = AlvysWriteOperationKind.TripAssign,
            Title = "Assign carrier and assets to trip",
            Description =
                "Assign a carrier and optionally a driver, truck, and trailer to an existing trip.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "trip-dispatch",
            Kind = AlvysWriteOperationKind.TripDispatch,
            Title = "Dispatch trip",
            Description =
                "Dispatch a trip that already has a carrier and assets assigned. The trip must be " +
                "covered before dispatching.",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "carrier-status-update",
            Kind = AlvysWriteOperationKind.CarrierStatusUpdate,
            Title = "Update carrier status",
            Description =
                "Update a carrier's operational status (e.g. Active, Inactive) with optimistic-" +
                "concurrency (ETag) protection.",
            WorkflowStage = "Assign",
            RequiresEtag = true,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },

        // --- Billing document writes (Public API surface, contracted 2026-07-21) -----------------
        new()
        {
            Code = "upload-load-document",
            Kind = AlvysWriteOperationKind.UploadLoadDocument,
            Title = "Upload load document",
            Description =
                "Upload a single billing document (Proof of Delivery, Bill of Lading, rate " +
                "confirmation, etc.) to a load via the contracted Alvys Public-API multipart " +
                "endpoint. Closes the missing-POD/BOL loop.",
            WorkflowStage = "Bill",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "upload-trip-document",
            Kind = AlvysWriteOperationKind.UploadTripDocument,
            Title = "Upload trip document",
            Description =
                "Upload a single billing/operational document to a trip via the contracted Alvys " +
                "Public-API multipart endpoint.",
            WorkflowStage = "Bill",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "create-carrier-invoice",
            Kind = AlvysWriteOperationKind.CreateCarrierInvoice,
            Title = "Attach carrier invoice",
            Description =
                "Attach a carrier invoice document to a trip via the contracted Alvys Public-API " +
                "multipart endpoint. Separately flag-gated (Alvys:Writeback:EnableCarrierInvoice) " +
                "because an unmatched PaymentType silently defaults to 30-day terms in Alvys.",
            WorkflowStage = "Bill",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },
        new()
        {
            Code = "create-customer-payment",
            Kind = AlvysWriteOperationKind.CreateCustomerPayment,
            Title = "Post customer payment",
            Description =
                "Post a customer payment against an invoiced load via the contracted Alvys Public-API " +
                "endpoint (POST /invoices/customer-payments). Amount{Amount,Currency} + PaymentDate + " +
                "ReferenceNumber (idempotency). The load must already be Invoiced/Completed/Financed " +
                "upstream; this closes the billed → paid loop.",
            WorkflowStage = "Bill",
            RequiresEtag = false,
            LiveSupport = AlvysLiveSupport.Supported,
            RequiredToEnable = null,
        },

        // --- Phase-2 consolidation writes (internal API surface) ---------------------------------
        // These are SCAFFOLDING. The internal-API endpoints are observed-not-contracted and their
        // exact routes are still pending discovery (see the discovered-endpoints table in
        // docs/ALVYS_API_DECISIONS.md, decision #10). They are marked Unsupported so live execution
        // is impossible until a real endpoint + session-token contract is confirmed; the wiring
        // proves the acquire/gate/build/record flow end-to-end against a fake handler only.
        new()
        {
            Code = "add-extended-stop",
            Kind = AlvysWriteOperationKind.AddExtendedStop,
            Title = "Add extended stop (Waypoint)",
            Description =
                "Create a Waypoint / extended stop on the parent trip during consolidation. " +
                "Dispatched against the Alvys internal API (observed, not contracted).",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            Surface = AlvysWriteApiSurface.Internal,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "Confirm the internal-API Waypoint-create endpoint (route + verb + body) from the " +
                "discovered-endpoints table in docs/ALVYS_API_DECISIONS.md (decision #10) and " +
                "confirm session-token access.",
        },
        new()
        {
            Code = "zero-child-dispatch-miles",
            Kind = AlvysWriteOperationKind.ZeroChildDispatchMiles,
            Title = "Zero child dispatch miles",
            Description =
                "Zero a consolidated child trip's dispatch (loaded) mileage while preserving " +
                "customer miles. Dispatched against the Alvys internal API (observed, not contracted).",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            Surface = AlvysWriteApiSurface.Internal,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "Confirm the internal-API trip-mileage update endpoint (route + verb + body) from " +
                "the discovered-endpoints table in docs/ALVYS_API_DECISIONS.md (decision #10) and " +
                "confirm session-token access.",
        },
        new()
        {
            Code = "set-trip-references",
            Kind = AlvysWriteOperationKind.SetTripReferences,
            Title = "Set trip references (LTL / main load id)",
            Description =
                "Set the LTL boolean and main_load_id references on a consolidated trip (both " +
                "transported as strings). Dispatched against the Alvys internal API (observed, not " +
                "contracted).",
            WorkflowStage = "Assign",
            RequiresEtag = false,
            Surface = AlvysWriteApiSurface.Internal,
            LiveSupport = AlvysLiveSupport.Unsupported,
            RequiredToEnable =
                "Confirm the internal-API trip-references update endpoint (route + verb + body) from " +
                "the discovered-endpoints table in docs/ALVYS_API_DECISIONS.md (decision #10) and " +
                "confirm session-token access.",
        },
    ];

    public static AlvysWriteOperationDescriptor? Find(string code) =>
        All.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
}
