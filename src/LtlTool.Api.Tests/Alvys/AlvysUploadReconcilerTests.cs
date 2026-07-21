using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Tests.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies post-write reconciliation for document uploads. A load-document upload is only reported
/// Confirmed when the re-fetched load documents actually contain the attachment; otherwise it is a
/// Mismatch surfaced for human review — never coerced to Confirmed, never auto-retried. Trip/invoice
/// uploads, which have no read-listing endpoint wired, resolve to an honest Pending.
/// </summary>
public sealed class AlvysUploadReconcilerTests
{
    private static AlvysOperationRequest LoadUpload() => new()
    {
        LoadNumber = "L-1001",
        DocumentType = "POD",
        FileBytes = [1, 2, 3],
        FileName = "pod.pdf",
        ContentType = "application/pdf",
    };

    [Fact]
    public async Task Confirmed_when_attachment_id_found_on_refetch()
    {
        var descriptor = AlvysWriteOperationRegistry.Find("upload-load-document")!;
        var alvys = new FakeAlvysClient
        {
            Documents = [new AlvysLoadDocument { Id = "att-77", AttachmentType = "POD" }],
        };
        var reconciler = new AlvysUploadReconciler(alvys);

        var outcome = await reconciler.ReconcileAsync(
            descriptor, LoadUpload(),
            new AlvysDocumentUploadResult { IsSuccess = true, StatusCode = 200, AttachmentId = "att-77" });

        Assert.Equal(AlvysReconciliationState.Confirmed, outcome.State);
    }

    [Fact]
    public async Task Mismatch_when_attachment_absent_on_refetch()
    {
        var descriptor = AlvysWriteOperationRegistry.Find("upload-load-document")!;
        var alvys = new FakeAlvysClient { Documents = [] };
        var reconciler = new AlvysUploadReconciler(alvys);

        var outcome = await reconciler.ReconcileAsync(
            descriptor, LoadUpload(),
            new AlvysDocumentUploadResult { IsSuccess = true, StatusCode = 200, AttachmentId = "att-77" });

        Assert.Equal(AlvysReconciliationState.Mismatch, outcome.State);
        Assert.Contains("human review", outcome.Detail);
    }

    [Fact]
    public async Task Trip_document_is_pending_no_listing_endpoint()
    {
        var descriptor = AlvysWriteOperationRegistry.Find("upload-trip-document")!;
        var reconciler = new AlvysUploadReconciler(new FakeAlvysClient());

        var outcome = await reconciler.ReconcileAsync(
            descriptor,
            new AlvysOperationRequest { TripId = "T-1", DocumentType = "Rate Confirmation", FileBytes = [1], FileName = "rc.pdf", ContentType = "application/pdf" },
            new AlvysDocumentUploadResult { IsSuccess = true, StatusCode = 200 });

        Assert.Equal(AlvysReconciliationState.Pending, outcome.State);
    }
}
