using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// The single boundary through which any Alvys write-oriented operation must pass. It builds and
/// validates the payload that <i>would</i> be sent, and decides the disposition based on the
/// configured <see cref="AlvysWritebackMode"/> and the operation's live-execution support.
///
/// <para>
/// The gateway itself never performs a live network call — it sets
/// <see cref="AlvysOperationOutcome.SandboxExecutionEligible"/> to signal the recorder that it
/// should dispatch the live sandbox call. Disabled mode yields audit-only; Simulation yields a
/// dry-run preview. The execute path and the dry-run path share payload construction so the preview
/// is exactly what would be sent.
/// </para>
/// </summary>
public interface IAlvysWriteGateway
{
    /// <summary>Builds and validates the payload for preview without ever executing.</summary>
    AlvysOperationOutcome DryRun(string operationCode, AlvysOperationRequest request);

    /// <summary>
    /// Attempts the operation. Honours the configured writeback mode and the operation's live
    /// support; in this phase it never sends to Alvys (see the type remarks).
    /// </summary>
    AlvysOperationOutcome Execute(string operationCode, AlvysOperationRequest request);
}

/// <inheritdoc cref="IAlvysWriteGateway"/>
public sealed class AlvysWriteGateway(
    IOptions<AlvysWriteOptions> writeOptions,
    IOptions<AlvysOptions> alvysOptions,
    IOptions<AlvysInternalApiOptions> internalOptions) : IAlvysWriteGateway
{
    private readonly AlvysWriteOptions _write = writeOptions.Value;
    private readonly AlvysOptions _alvys = alvysOptions.Value;
    private readonly AlvysInternalApiOptions _internal = internalOptions.Value;

    /// <summary>
    /// Convenience overload for callers/tests that only exercise the Public-API surface. The internal
    /// API defaults to disabled, so internal operations resolve to audit-only.
    /// </summary>
    public AlvysWriteGateway(
        IOptions<AlvysWriteOptions> writeOptions,
        IOptions<AlvysOptions> alvysOptions)
        : this(writeOptions, alvysOptions,
            Microsoft.Extensions.Options.Options.Create(new AlvysInternalApiOptions()))
    {
    }

    public AlvysOperationOutcome DryRun(string operationCode, AlvysOperationRequest request)
        => Build(operationCode, request, executing: false);

    public AlvysOperationOutcome Execute(string operationCode, AlvysOperationRequest request)
        => Build(operationCode, request, executing: true);

    private AlvysOperationOutcome Build(string operationCode, AlvysOperationRequest request, bool executing)
    {
        var op = AlvysWriteOperationRegistry.Find(operationCode);
        if (op is null)
        {
            return new AlvysOperationOutcome
            {
                OperationCode = operationCode,
                Title = operationCode,
                Mode = _write.Mode,
                Disposition = AlvysOperationDisposition.Unsupported,
                Message = $"Unknown operation '{operationCode}'.",
                Blockers = ["The operation code is not in the Alvys write-operation registry."],
            };
        }

        var issues = Validate(op, request, _write);
        if (issues.Count > 0)
        {
            return new AlvysOperationOutcome
            {
                OperationCode = op.Code,
                Title = op.Title,
                Mode = _write.Mode,
                Disposition = AlvysOperationDisposition.Blocked,
                Message = "The request is incomplete or invalid; nothing was sent.",
                Validation = issues,
                RequiredToEnable = op.RequiredToEnable,
            };
        }

        var payload = BuildPayload(op, request);

        // Internal-API surface (Phase-2 consolidation) has its own gate and disposition set,
        // separate from the Public-API sandbox path.
        if (op.Surface == AlvysWriteApiSurface.Internal)
            return BuildInternal(op, request, payload, executing);

        var blockers = SandboxBlockers(op);

        // Decide disposition. Sandbox execution is gated by mode + config blockers + live support;
        // when all gates pass on the execute path the recorder dispatches the live call.
        AlvysOperationDisposition disposition;
        string message;
        var sandboxExecutionEligible = false;

        if (op.LiveSupport == AlvysLiveSupport.Unsupported)
        {
            disposition = _write.Mode switch
            {
                AlvysWritebackMode.Disabled => AlvysOperationDisposition.AuditOnly,
                AlvysWritebackMode.Simulation => AlvysOperationDisposition.Simulated,
                _ => AlvysOperationDisposition.Unsupported,
            };
            message = disposition switch
            {
                AlvysOperationDisposition.AuditOnly =>
                    "Writeback is disabled — payload recorded for audit only, not sent to Alvys.",
                AlvysOperationDisposition.Simulated =>
                    "Simulation only — payload built and validated, not sent to Alvys.",
                _ =>
                    "Operation is not yet supported for live sandbox execution (no documented " +
                    "Alvys endpoint). Nothing was sent.",
            };
        }
        else
        {
            // Supported operation. Eligible to execute only when sandbox mode is fully configured.
            if (_write.Mode != AlvysWritebackMode.Sandbox || blockers.Count > 0)
            {
                disposition = _write.Mode == AlvysWritebackMode.Disabled
                    ? AlvysOperationDisposition.AuditOnly
                    : AlvysOperationDisposition.Simulated;
                message = blockers.Count > 0
                    ? "Sandbox writeback is not ready — payload simulated only, not sent."
                    : "Payload simulated only, not sent to Alvys.";
            }
            else if (!executing)
            {
                disposition = AlvysOperationDisposition.Simulated;
                message = "Dry run — payload eligible for sandbox execution but not sent.";
            }
            else
            {
                // All gates passed: the recorder will dispatch the live sandbox call and update
                // the disposition to SandboxExecuted on success.
                disposition = AlvysOperationDisposition.SandboxExecuted;
                message = "Sandbox execution eligible — dispatching to Alvys sandbox.";
                sandboxExecutionEligible = true;
            }
        }

        return new AlvysOperationOutcome
        {
            OperationCode = op.Code,
            Title = op.Title,
            Mode = _write.Mode,
            Disposition = disposition,
            Executed = false,
            SandboxExecutionEligible = sandboxExecutionEligible,
            Message = message,
            Payload = payload,
            Blockers = blockers,
            RequiredToEnable = op.LiveSupport == AlvysLiveSupport.Unsupported ? op.RequiredToEnable : null,
        };
    }

    /// <summary>
    /// Resolves the disposition for an internal-API (Phase-2 consolidation) operation. Dispatch is
    /// gated by <see cref="AlvysInternalApiOptions"/>: the surface must be enabled, a base URL must be
    /// configured, and the specific operation must be individually armed. Every gate defaults to off,
    /// so a fresh clone / CI / production never dispatches an internal write. When all gates pass on
    /// the execute path the recorder dispatches via <see cref="IAlvysInternalWriteClient"/>.
    /// </summary>
    private AlvysOperationOutcome BuildInternal(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        bool executing)
    {
        var blockers = InternalBlockers(op);

        AlvysOperationDisposition disposition;
        string message;
        var eligible = false;

        if (blockers.Count > 0)
        {
            // Not armed/configured — recorded for audit only, never sent.
            disposition = AlvysOperationDisposition.AuditOnly;
            message = "Internal-API write is not armed/configured — payload recorded for audit only, "
                + "not sent to Alvys.";
        }
        else if (!executing)
        {
            disposition = AlvysOperationDisposition.Simulated;
            message = "Dry run — payload eligible for internal-API execution but not sent.";
        }
        else
        {
            // All gates passed: the recorder dispatches the internal call and updates the disposition
            // to InternalExecuted on success or InternalFailed on failure.
            disposition = AlvysOperationDisposition.InternalExecuted;
            message = "Internal-API execution eligible — dispatching to the Alvys internal API.";
            eligible = true;
        }

        return new AlvysOperationOutcome
        {
            OperationCode = op.Code,
            Title = op.Title,
            Mode = _write.Mode,
            Disposition = disposition,
            Executed = false,
            InternalExecutionEligible = eligible,
            Message = message,
            Payload = payload,
            Blockers = blockers,
            RequiredToEnable = eligible ? null : op.RequiredToEnable,
        };
    }

    /// <summary>
    /// Reasons an internal-API operation cannot dispatch under the current configuration. Mirrors the
    /// sandbox gate posture: enabling the surface alone is never enough — each operation must also be
    /// individually armed.
    /// </summary>
    private List<string> InternalBlockers(AlvysWriteOperationDescriptor op)
    {
        var blockers = new List<string>();
        if (!_internal.Enabled)
            blockers.Add("The Alvys internal API is disabled (Alvys:InternalApi:Enabled=false).");
        if (!_internal.HasBaseUrl)
            blockers.Add("No Alvys internal API base URL is configured (Alvys:InternalApi:BaseUrl).");
        if (!_internal.IsOperationArmed(op.Kind))
            blockers.Add($"Operation '{op.Code}' is not individually armed for internal-API execution.");
        return blockers;
    }

    /// <summary>Per-operation required-input validation. Pure; no upstream calls.</summary>
    private static List<AlvysOperationIssue> Validate(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request, AlvysWriteOptions write)
    {
        var issues = new List<AlvysOperationIssue>();

        void Require(bool present, string code, string message)
        {
            if (!present) issues.Add(new AlvysOperationIssue { Code = code, Message = message });
        }

        switch (op.Kind)
        {
            case AlvysWriteOperationKind.CreateLoadNote:
                Require(!string.IsNullOrWhiteSpace(request.LoadNumber), "LOAD_NUMBER_REQUIRED",
                    "A load number is required.");
                Require(!string.IsNullOrWhiteSpace(request.NoteText), "NOTE_TEXT_REQUIRED",
                    "Note text is required.");
                // NoteType must be one of the four Alvys-accepted values.
                if (!string.IsNullOrWhiteSpace(request.NoteType) &&
                    !AlvysNoteTypes.IsValid(request.NoteType))
                {
                    issues.Add(new AlvysOperationIssue
                    {
                        Code = "NOTE_TYPE_INVALID",
                        Message = $"NoteType '{request.NoteType}' is not valid. " +
                                  $"Must be one of: {string.Join(", ", AlvysNoteTypes.All)}.",
                    });
                }
                break;

            case AlvysWriteOperationKind.TenderAccept:
                Require(!string.IsNullOrWhiteSpace(request.TenderId), "TENDER_ID_REQUIRED",
                    "A tender id is required.");
                Require(request.StopCompanyLinks is { Count: > 0 }, "STOP_COMPANY_LINKS_REQUIRED",
                    "At least one StopCompanyLink (StopId + CompanyId) is required to accept a tender.");
                // Every link must carry both ids — blanks must never reach Alvys.
                foreach (var link in request.StopCompanyLinks ?? [])
                {
                    Require(!string.IsNullOrWhiteSpace(link.StopId) && !string.IsNullOrWhiteSpace(link.CompanyId),
                        "STOP_COMPANY_LINK_INVALID",
                        "Each StopCompanyLink must include a non-empty StopId and CompanyId.");
                }
                break;

            case AlvysWriteOperationKind.TripStopArrival:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                Require(!string.IsNullOrWhiteSpace(request.StopId), "STOP_ID_REQUIRED",
                    "A stop id is required.");
                Require(request.ArrivedAt is not null, "ARRIVED_AT_REQUIRED",
                    "An arrival timestamp is required.");
                break;

            case AlvysWriteOperationKind.TripStopDeparture:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                Require(!string.IsNullOrWhiteSpace(request.StopId), "STOP_ID_REQUIRED",
                    "A stop id is required.");
                Require(request.DepartedAt is not null, "DEPARTED_AT_REQUIRED",
                    "A departure timestamp is required.");
                break;

            case AlvysWriteOperationKind.LoadUpdate:
                Require(!string.IsNullOrWhiteSpace(request.LoadNumber), "LOAD_NUMBER_REQUIRED",
                    "A load number is required.");
                Require(request.Fields is { Count: > 0 }, "FIELDS_REQUIRED",
                    "At least one field to update is required. Currently only 'OrderNumber' is " +
                    "writable via this endpoint (max 30 chars).");
                // Allowlist: only documented-writable fields may reach the live PATCH body.
                foreach (var (key, value) in request.Fields ?? [])
                {
                    if (!AlvysLoadUpdateFields.IsWritable(key))
                    {
                        issues.Add(new AlvysOperationIssue
                        {
                            Code = "FIELD_NOT_WRITABLE",
                            Message = $"Field '{key}' is not writable via load-update. " +
                                      $"Allowed: {string.Join(", ", AlvysLoadUpdateFields.Writable)}.",
                        });
                        continue;
                    }
                    if (AlvysLoadUpdateFields.IsOrderNumber(key))
                    {
                        Require(!string.IsNullOrWhiteSpace(value), "ORDER_NUMBER_BLANK",
                            "OrderNumber cannot be blank.");
                        Require(value is null || value.Length <= AlvysLoadUpdateFields.OrderNumberMaxLength,
                            "ORDER_NUMBER_TOO_LONG",
                            $"OrderNumber must be {AlvysLoadUpdateFields.OrderNumberMaxLength} characters or fewer.");
                    }
                }
                break;

            case AlvysWriteOperationKind.TripAssign:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                Require(!string.IsNullOrWhiteSpace(request.CarrierId), "CARRIER_ID_REQUIRED",
                    "A carrier id is required to assign to the trip.");
                break;

            case AlvysWriteOperationKind.TripDispatch:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                break;

            case AlvysWriteOperationKind.CarrierStatusUpdate:
                Require(!string.IsNullOrWhiteSpace(request.CarrierId), "CARRIER_ID_REQUIRED",
                    "A carrier id is required.");
                Require(!string.IsNullOrWhiteSpace(request.Status), "STATUS_REQUIRED",
                    "A status value is required (e.g. Active, Inactive).");
                break;

            case AlvysWriteOperationKind.UploadLoadDocument:
                Require(!string.IsNullOrWhiteSpace(request.LoadNumber), "LOAD_NUMBER_REQUIRED",
                    "A load number is required.");
                ValidateDocumentUpload(issues, request,
                    AlvysLoadDocumentTypes.IsValid(request.DocumentType), AlvysLoadDocumentTypes.All,
                    AlvysDocumentUploadLimits.LoadContentTypes, AlvysDocumentUploadLimits.LoadMaxBytes);
                break;

            case AlvysWriteOperationKind.UploadTripDocument:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                ValidateDocumentUpload(issues, request,
                    AlvysTripDocumentTypes.IsValid(request.DocumentType), AlvysTripDocumentTypes.All,
                    AlvysDocumentUploadLimits.TripContentTypes, AlvysDocumentUploadLimits.TripMaxBytes);
                break;

            case AlvysWriteOperationKind.CreateCarrierInvoice:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required to attach a carrier invoice.");
                Require(request.FileBytes is { Length: > 0 }, "FILE_REQUIRED",
                    "A file is required.");
                Require(request.FileBytes is null || request.FileBytes.LongLength <= AlvysDocumentUploadLimits.TripMaxBytes,
                    "FILE_TOO_LARGE",
                    $"The file exceeds the {AlvysDocumentUploadLimits.TripMaxBytes / (1024 * 1024)}MB limit.");
                // PaymentType is optional, but if supplied it MUST be on the operator whitelist —
                // an unmatched value silently defaults to 30-day terms in Alvys, so we refuse it.
                if (!string.IsNullOrWhiteSpace(request.PaymentType) && !write.IsAllowedPaymentType(request.PaymentType))
                {
                    issues.Add(new AlvysOperationIssue
                    {
                        Code = "PAYMENT_TYPE_NOT_ALLOWED",
                        Message = $"PaymentType '{request.PaymentType}' is not on the allowed whitelist " +
                                  "(Alvys silently defaults unknown values to 30-day terms).",
                    });
                }
                break;

            case AlvysWriteOperationKind.AddExtendedStop:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A parent trip id is required.");
                Require(request.WaypointStop is not null, "WAYPOINT_REQUIRED",
                    "A waypoint stop (CompanyId + Sequence) is required.");
                if (request.WaypointStop is { } wp)
                {
                    Require(!string.IsNullOrWhiteSpace(wp.CompanyId), "WAYPOINT_COMPANY_REQUIRED",
                        "The waypoint requires a CompanyId.");
                    Require(wp.Sequence >= 0, "WAYPOINT_SEQUENCE_INVALID",
                        "The waypoint sequence must be zero or greater.");
                }
                Require(!string.IsNullOrWhiteSpace(request.ActingUserId), "ACTING_USER_REQUIRED",
                    "An acting user id is required to authorise an internal-API write.");
                break;

            case AlvysWriteOperationKind.ZeroChildDispatchMiles:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A child trip id is required.");
                Require(!string.IsNullOrWhiteSpace(request.ActingUserId), "ACTING_USER_REQUIRED",
                    "An acting user id is required to authorise an internal-API write.");
                break;

            case AlvysWriteOperationKind.SetTripReferences:
                Require(!string.IsNullOrWhiteSpace(request.TripId), "TRIP_ID_REQUIRED",
                    "A trip id is required.");
                Require(request.LtlReference is not null || !string.IsNullOrWhiteSpace(request.MainLoadId),
                    "REFERENCE_REQUIRED",
                    "At least one reference (LtlReference or MainLoadId) is required.");
                Require(!string.IsNullOrWhiteSpace(request.ActingUserId), "ACTING_USER_REQUIRED",
                    "An acting user id is required to authorise an internal-API write.");
                break;
        }

        // ETag is mandatory for mutate-existing operations so a concurrent change can never be
        // silently clobbered once live execution is enabled.
        if (op.RequiresEtag)
            Require(!string.IsNullOrWhiteSpace(request.Etag), "ETAG_REQUIRED",
                "An ETag/concurrency token is required for this operation.");

        return issues;
    }

    /// <summary>
    /// Shared validation for a document upload: a non-empty file within the size limit, an allowed
    /// content type, and a DocumentType on the endpoint's allowlist. Additive to <paramref name="issues"/>.
    /// </summary>
    private static void ValidateDocumentUpload(
        List<AlvysOperationIssue> issues, AlvysOperationRequest request,
        bool documentTypeValid, IReadOnlyList<string> allowedTypes,
        IReadOnlyList<string> allowedContentTypes, long maxBytes)
    {
        if (request.FileBytes is not { Length: > 0 })
            issues.Add(new AlvysOperationIssue { Code = "FILE_REQUIRED", Message = "A file is required." });
        else if (request.FileBytes.LongLength > maxBytes)
            issues.Add(new AlvysOperationIssue
            {
                Code = "FILE_TOO_LARGE",
                Message = $"The file exceeds the {maxBytes / (1024 * 1024)}MB limit.",
            });

        if (!AlvysDocumentUploadLimits.IsAllowedContentType(allowedContentTypes, request.ContentType))
            issues.Add(new AlvysOperationIssue
            {
                Code = "CONTENT_TYPE_NOT_ALLOWED",
                Message = $"Content type '{request.ContentType}' is not allowed. " +
                          $"Allowed: {string.Join(", ", allowedContentTypes)}.",
            });

        if (string.IsNullOrWhiteSpace(request.DocumentType))
            issues.Add(new AlvysOperationIssue { Code = "DOCUMENT_TYPE_REQUIRED", Message = "A DocumentType is required." });
        else if (!documentTypeValid)
            issues.Add(new AlvysOperationIssue
            {
                Code = "DOCUMENT_TYPE_INVALID",
                Message = $"DocumentType '{request.DocumentType}' is not valid. " +
                          $"Must be one of: {string.Join(", ", allowedTypes)}.",
            });
    }

    /// <summary>Builds the concrete preview body for an operation. Pure; no upstream calls.</summary>
    private static AlvysOperationPayload BuildPayload(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request)
    {
        var body = new Dictionary<string, object?>();
        string target;

        switch (op.Kind)
        {
            case AlvysWriteOperationKind.CreateLoadNote:
                // Alvys requires a client-supplied Id; it is generated by the write client at
                // dispatch time so it stays out of the deterministic payload hash / idempotency key.
                body["Description"] = request.NoteText;
                body["NoteType"] = string.IsNullOrWhiteSpace(request.NoteType) || !AlvysNoteTypes.IsValid(request.NoteType)
                    ? AlvysNoteTypes.General : request.NoteType.Trim();
                target = $"POST /loads/{request.LoadNumber}/notes";
                break;

            case AlvysWriteOperationKind.TenderAccept:
                // TenderId goes in the path; body is StopCompanyLinks + optional FleetId.
                body["StopCompanyLinks"] = (request.StopCompanyLinks ?? [])
                    .Select(l => new Dictionary<string, object?> { ["StopId"] = l.StopId, ["CompanyId"] = l.CompanyId })
                    .ToArray();
                if (!string.IsNullOrWhiteSpace(request.FleetId))
                    body["FleetId"] = request.FleetId;
                target = $"POST /tenders/{request.TenderId}/accept";
                break;

            case AlvysWriteOperationKind.TripStopArrival:
                body["ArrivedAt"] = request.ArrivedAt;
                target = $"PUT /trips/{request.TripId}/stops/{request.StopId}/arrival";
                break;

            case AlvysWriteOperationKind.TripStopDeparture:
                body["DepartedAt"] = request.DepartedAt;
                target = $"PUT /trips/{request.TripId}/stops/{request.StopId}/departure";
                break;

            case AlvysWriteOperationKind.LoadUpdate:
                foreach (var (key, value) in request.Fields ?? [])
                    body[key] = value;
                target = $"PATCH /loads/{request.LoadNumber}";
                break;

            case AlvysWriteOperationKind.TripAssign:
                body["CarrierId"] = request.CarrierId;
                if (!string.IsNullOrWhiteSpace(request.DriverId)) body["DriverId"] = request.DriverId;
                if (!string.IsNullOrWhiteSpace(request.TruckId)) body["TruckId"] = request.TruckId;
                if (!string.IsNullOrWhiteSpace(request.TrailerId)) body["TrailerId"] = request.TrailerId;
                target = $"POST /trips/{request.TripId}/assign";
                break;

            case AlvysWriteOperationKind.TripDispatch:
                // Body is intentionally empty — dispatch is a state transition on the trip.
                target = $"POST /trips/{request.TripId}/dispatch";
                break;

            case AlvysWriteOperationKind.CarrierStatusUpdate:
                body["Status"] = request.Status;
                target = $"PATCH /carriers/{request.CarrierId}/status";
                break;

            case AlvysWriteOperationKind.AddExtendedStop:
                // Internal API (observed, not contracted). CompanyId + zero-based sequence identify
                // where the new Waypoint lands in the parent trip's stop order.
                body["CompanyId"] = request.WaypointStop!.CompanyId;
                body["Sequence"] = request.WaypointStop.Sequence;
                if (request.WaypointStop.ScheduledAt is { } scheduled)
                    body["ScheduledAt"] = scheduled;
                target = $"POST internal trip {request.TripId} waypoint";
                break;

            case AlvysWriteOperationKind.ZeroChildDispatchMiles:
                // Only dispatch (loaded) mileage is zeroed; customer miles are preserved upstream and
                // are deliberately not part of this payload (decision #10).
                body["DispatchMiles"] = 0;
                target = $"PATCH internal trip {request.TripId} dispatch-miles";
                break;

            case AlvysWriteOperationKind.SetTripReferences:
                // Both references transport as strings (decision #10): LTL as "true"/"false", the
                // main load id verbatim.
                if (request.LtlReference is { } ltl)
                    body["LTL"] = ltl ? "true" : "false";
                if (!string.IsNullOrWhiteSpace(request.MainLoadId))
                    body["MainLoadId"] = request.MainLoadId;
                target = $"PATCH internal trip {request.TripId} references";
                break;

            case AlvysWriteOperationKind.UploadLoadDocument:
                // Metadata only — the raw bytes never enter the payload/hash/preview. The multipart
                // body is assembled by the upload client at dispatch time from FileBytes directly.
                body["DocumentType"] = AlvysLoadDocumentTypes.Canonical(request.DocumentType) ?? request.DocumentType?.Trim();
                body["FileName"] = request.FileName;
                body["ContentType"] = request.ContentType;
                body["FileSizeBytes"] = request.FileBytes?.LongLength ?? 0L;
                target = $"POST /loads/{request.LoadNumber}/document (multipart)";
                break;

            case AlvysWriteOperationKind.UploadTripDocument:
                body["DocumentType"] = AlvysTripDocumentTypes.Canonical(request.DocumentType) ?? request.DocumentType?.Trim();
                body["FileName"] = request.FileName;
                body["ContentType"] = request.ContentType;
                body["FileSizeBytes"] = request.FileBytes?.LongLength ?? 0L;
                target = $"POST /trips/{request.TripId}/document (multipart)";
                break;

            case AlvysWriteOperationKind.CreateCarrierInvoice:
                body["TripId"] = request.TripId;
                body["FileName"] = request.FileName;
                body["ContentType"] = request.ContentType;
                body["FileSizeBytes"] = request.FileBytes?.LongLength ?? 0L;
                if (!string.IsNullOrWhiteSpace(request.CarrierInvoiceNumber))
                    body["CarrierInvoiceNumber"] = request.CarrierInvoiceNumber.Trim();
                if (!string.IsNullOrWhiteSpace(request.PaymentType))
                    body["PaymentType"] = request.PaymentType.Trim();
                target = "POST /invoices/carrier-invoice (multipart)";
                break;

            default:
                target = op.Title;
                break;
        }

        return new AlvysOperationPayload
        {
            OperationCode = op.Code,
            TargetDescription = target,
            RequiresEtag = op.RequiresEtag,
            EtagSupplied = !string.IsNullOrWhiteSpace(request.Etag),
            Body = body,
        };
    }

    /// <summary>
    /// Top-level reasons a (hypothetically supported) operation could not execute against the
    /// sandbox under the current configuration. Used for both the readiness panel and the gateway.
    /// </summary>
    private List<string> SandboxBlockers(AlvysWriteOperationDescriptor op)
    {
        var blockers = new List<string>();

        if (_write.Mode != AlvysWritebackMode.Sandbox)
            blockers.Add($"Writeback mode is {_write.Mode}, not Sandbox.");
        else
        {
            if (!_write.IsRecognisedSandboxEnvironment)
                blockers.Add(
                    $"Environment '{_write.Environment}' is not a recognised non-production sandbox.");
            if (!_write.HasSandboxBaseUrl)
                blockers.Add("No sandbox base URL is configured (or it points at the production host).");
            if (!_alvys.HasCredentials)
                blockers.Add("Alvys credentials are not configured.");
        }

        // The carrier-invoice attach carries a silent-default risk (unknown PaymentType → 30-day
        // terms upstream), so it needs a dedicated arm switch in addition to sandbox mode.
        if (op.Kind == AlvysWriteOperationKind.CreateCarrierInvoice && !_write.EnableCarrierInvoice)
            blockers.Add(
                "The carrier-invoice write is not armed (Alvys:Writeback:EnableCarrierInvoice=false).");

        return blockers;
    }
}
