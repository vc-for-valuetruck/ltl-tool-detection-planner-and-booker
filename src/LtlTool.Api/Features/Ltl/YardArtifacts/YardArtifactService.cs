using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Yard-artifact intake boundary. Validates a submission, persists the photo/PDF bytes to the file
/// store, computes the honest inspection roll-up (Submitted / Passed / Flagged from the answered
/// items — never guessed), records the SQL metadata, and maps records to the surfacing read-model.
/// Dock-verified pallet counts/dims are carried through with a "yard verification" source so the
/// enrichment / trailer-fit layer can prefer them over EDI estimates. Internal data only — Alvys is
/// never read or written here.
/// </summary>
public sealed class YardArtifactService(
    IYardArtifactStore store,
    IYardArtifactFileStore files,
    Microsoft.Extensions.Options.IOptions<YardArtifactOptions> options,
    TimeProvider clock)
{
    public async Task<YardArtifactView> CreateAsync(
        YardArtifactSubmission submission,
        IReadOnlyList<YardArtifactUpload> uploads,
        string submittedBy,
        CancellationToken ct)
    {
        var opts = options.Value;

        var yard = NormalizeYard(submission.Yard);
        var truck = Trim(submission.TruckUnit);
        var trailer = Trim(submission.TrailerUnit);
        var load = Trim(submission.LoadNumber);

        if (truck is null && trailer is null && load is null)
        {
            throw new YardArtifactValidationException(
                "At least one of truckUnit, trailerUnit or loadNumber is required to key the artifact.");
        }

        if (uploads.Count > opts.MaxFiles)
        {
            throw new YardArtifactValidationException(
                $"Too many files: {uploads.Count} exceeds the limit of {opts.MaxFiles}.");
        }

        foreach (var upload in uploads)
        {
            ValidateUpload(upload, opts);
        }

        var id = Guid.NewGuid().ToString("n");

        var stored = new List<YardArtifactFile>(uploads.Count);
        foreach (var upload in uploads)
        {
            stored.Add(await files.SaveAsync(
                id, upload.Kind, upload.FileName, upload.ContentType, upload.Content, ct));
        }

        var (passed, failed, na, status) = Summarize(submission.Items);
        var now = clock.GetUtcNow();

        var record = new YardArtifactRecord
        {
            Id = id,
            Yard = yard,
            TruckUnit = truck,
            TrailerUnit = trailer,
            LoadNumber = load,
            SubmittedBy = submittedBy,
            CapturedAt = submission.CapturedAt ?? now,
            CreatedAt = now,
            Status = status,
            PassedItems = passed,
            FailedItems = failed,
            NaItems = na,
            VerifiedPalletCount = submission.VerifiedPalletCount,
            VerifiedLengthInches = submission.VerifiedDims?.LengthInches,
            VerifiedWidthInches = submission.VerifiedDims?.WidthInches,
            VerifiedHeightInches = submission.VerifiedDims?.HeightInches,
            InspectionJson = JsonSerializer.Serialize(submission, YardArtifactMapping.Json),
            FilesJson = JsonSerializer.Serialize(stored, YardArtifactMapping.Json),
        };

        store.Add(record);
        return YardArtifactMapping.ToView(record);
    }

    public YardArtifactView? Get(string id)
    {
        var record = store.Get(id);
        return record is null ? null : YardArtifactMapping.ToView(record);
    }

    public IReadOnlyList<YardArtifactView> Query(YardArtifactQuery query) =>
        store.Query(query).Select(YardArtifactMapping.ToView).ToArray();

    public YardArtifactStreamedFile? OpenFile(string artifactId, string fileId)
    {
        var record = store.Get(artifactId);
        if (record is null) return null;

        var file = YardArtifactMapping.Files(record).FirstOrDefault(f => f.Id == fileId);
        return file is null ? null : files.Open(file);
    }

    private static void ValidateUpload(YardArtifactUpload upload, YardArtifactOptions opts)
    {
        var contentType = upload.ContentType ?? string.Empty;
        var ok = upload.Kind switch
        {
            YardArtifactFileKind.Photo => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            YardArtifactFileKind.Pdf => contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!ok)
        {
            throw new YardArtifactValidationException(
                $"Unsupported content type '{contentType}' for a {upload.Kind} upload.");
        }

        // Length is available for buffered form files; when unknown (-1) we defer to the request
        // body size limit enforced by the pipeline.
        if (upload.Content.CanSeek && upload.Content.Length > opts.MaxFileBytes)
        {
            throw new YardArtifactValidationException(
                $"File '{upload.FileName}' exceeds the {opts.MaxFileBytes / (1024 * 1024)} MB limit.");
        }
    }

    private static (int Passed, int Failed, int Na, YardInspectionStatus Status) Summarize(
        List<YardInspectionItemInput>? items)
    {
        if (items is null || items.Count == 0)
        {
            return (0, 0, 0, YardInspectionStatus.Submitted);
        }

        int passed = 0, failed = 0, na = 0;
        foreach (var item in items)
        {
            switch ((item.Result ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "pass":
                    passed++;
                    break;
                case "fail":
                    failed++;
                    break;
                case "na":
                case "n/a":
                    na++;
                    break;
            }
        }

        var status = failed > 0
            ? YardInspectionStatus.Flagged
            : (passed + na > 0 ? YardInspectionStatus.Passed : YardInspectionStatus.Submitted);

        return (passed, failed, na, status);
    }

    private static string NormalizeYard(string? yard)
    {
        var value = Trim(yard);
        if (value is null)
        {
            throw new YardArtifactValidationException("yard is required (e.g. LAREDO or DALLAS).");
        }
        return value.ToUpperInvariant();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
