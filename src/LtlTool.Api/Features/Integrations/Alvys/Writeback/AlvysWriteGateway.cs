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
    IOptions<AlvysOptions> alvysOptions) : IAlvysWriteGateway
{
    private readonly AlvysWriteOptions _write = writeOptions.Value;
    private readonly AlvysOptions _alvys = alvysOptions.Value;

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

        var issues = Validate(op, request);
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

    /// <summary>Per-operation required-input validation. Pure; no upstream calls.</summary>
    private static List<AlvysOperationIssue> Validate(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request)
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
                    "At least one field to update is required.");
                break;
        }

        // ETag is mandatory for mutate-existing operations so a concurrent change can never be
        // silently clobbered once live execution is enabled.
        if (op.RequiresEtag)
            Require(!string.IsNullOrWhiteSpace(request.Etag), "ETAG_REQUIRED",
                "An ETag/concurrency token is required for this operation.");

        return issues;
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
                // Alvys requires a client-supplied Id, Description, and a valid NoteType.
                body["Id"] = Guid.NewGuid().ToString();
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

        return blockers;
    }
}
