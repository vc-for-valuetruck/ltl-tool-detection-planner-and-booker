namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Post-write reconciliation for document uploads. A 2xx upload response is not proof the attachment
/// landed, so after a successful load-document upload the reconciler re-fetches the load's documents
/// and confirms the new attachment id/type is present. A mismatch (or a re-fetch failure) is surfaced
/// as <see cref="AlvysReconciliationState.Mismatch"/> for human review — never coerced to Confirmed,
/// never auto-retried.
///
/// <para>
/// Trip-document and carrier-invoice uploads have no read-listing endpoint wired in this slice, so
/// they resolve to <see cref="AlvysReconciliationState.Pending"/> with an honest detail rather than a
/// fabricated confirmation.
/// </para>
/// </summary>
public interface IAlvysUploadReconciler
{
    Task<AlvysReconciliationOutcome> ReconcileAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysDocumentUploadResult uploadResult,
        CancellationToken ct = default);
}

/// <summary>The reconciliation state plus a human-readable detail for the ops panel.</summary>
public sealed record AlvysReconciliationOutcome(AlvysReconciliationState State, string Detail);

/// <inheritdoc cref="IAlvysUploadReconciler"/>
public sealed class AlvysUploadReconciler(IAlvysClient alvysClient) : IAlvysUploadReconciler
{
    public async Task<AlvysReconciliationOutcome> ReconcileAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysDocumentUploadResult uploadResult,
        CancellationToken ct = default)
    {
        if (op.Kind != AlvysWriteOperationKind.UploadLoadDocument)
        {
            return new AlvysReconciliationOutcome(
                AlvysReconciliationState.Pending,
                "Upload accepted by Alvys; no read-listing endpoint is wired for this resource, so the "
                + "attachment could not be re-fetched to confirm. Left pending for manual verification.");
        }

        IReadOnlyList<AlvysLoadDocument> documents;
        try
        {
            documents = await alvysClient.ListLoadDocumentsAsync(request.LoadNumber!, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new AlvysReconciliationOutcome(
                AlvysReconciliationState.Mismatch,
                $"Upload returned success but the confirming re-fetch failed ({ex.GetType().Name}); "
                + "surfaced for human review, not auto-retried.");
        }

        // Prefer matching the returned attachment id; fall back to matching the DocumentType when the
        // upload response omitted an id.
        var canonicalType = AlvysLoadDocumentTypes.Canonical(request.DocumentType);
        var match = documents.FirstOrDefault(d =>
            (!string.IsNullOrWhiteSpace(uploadResult.AttachmentId)
                && string.Equals(d.Id, uploadResult.AttachmentId, StringComparison.OrdinalIgnoreCase))
            || (string.IsNullOrWhiteSpace(uploadResult.AttachmentId)
                && !string.IsNullOrWhiteSpace(canonicalType)
                && string.Equals(d.AttachmentType, canonicalType, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            return new AlvysReconciliationOutcome(
                AlvysReconciliationState.Confirmed,
                $"Confirmed: attachment '{match.Id}' ({match.AttachmentType}) found on load "
                + $"{request.LoadNumber} after re-fetch.");
        }

        return new AlvysReconciliationOutcome(
            AlvysReconciliationState.Mismatch,
            "Upload returned success but the re-fetched load documents did not include the expected "
            + "attachment; surfaced for human review, not auto-retried.");
    }
}

/// <summary>
/// No-op <see cref="IAlvysUploadReconciler"/> for unit tests that do not exercise reconciliation.
/// Reports <see cref="AlvysReconciliationState.NotApplicable"/> without any upstream call.
/// </summary>
public sealed class NoOpAlvysUploadReconciler : IAlvysUploadReconciler
{
    public Task<AlvysReconciliationOutcome> ReconcileAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysDocumentUploadResult uploadResult,
        CancellationToken ct = default) =>
        Task.FromResult(new AlvysReconciliationOutcome(
            AlvysReconciliationState.NotApplicable, "Reconciliation not performed."));
}
