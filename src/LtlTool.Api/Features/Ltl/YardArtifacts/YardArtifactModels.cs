using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Rolled-up state of a submitted Level-1 yard inspection. Derived from the answered items, never
/// guessed: no answered items → <see cref="Submitted"/>; any Fail → <see cref="Flagged"/>; otherwise
/// <see cref="Passed"/>. The dock worker's submission is the source of truth — Alvys is never consulted
/// or written for yard artifacts.
/// </summary>
public enum YardInspectionStatus
{
    Submitted,
    Passed,
    Flagged,
}

/// <summary>Kind of stored file attached to a yard artifact.</summary>
public enum YardArtifactFileKind
{
    Photo,
    Pdf,
}

/// <summary>
/// Durable metadata row for one yard-artifact submission (a completed Level-1 inspection plus its
/// photos/PDF). The inspection payload and the file descriptor list are stored as serialized JSON —
/// they round-trip verbatim and never participate in relational queries; only the equipment/load keys
/// and yard/timestamp do. Photo/PDF bytes live in the file store (<see cref="IYardArtifactFileStore"/>),
/// not in SQL. Nothing here touches Alvys: yard artifacts are our internal data.
/// </summary>
public sealed class YardArtifactRecord
{
    public required string Id { get; set; }

    /// <summary>Yard the artifact was captured at (normalized upper-case, e.g. <c>LAREDO</c>/<c>DALLAS</c>).</summary>
    public required string Yard { get; set; }

    public string? TruckUnit { get; set; }
    public string? TrailerUnit { get; set; }
    public string? LoadNumber { get; set; }

    /// <summary>The user (email/upn) who submitted the artifact from the dock.</summary>
    public required string SubmittedBy { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public YardInspectionStatus Status { get; set; }
    public int PassedItems { get; set; }
    public int FailedItems { get; set; }
    public int NaItems { get; set; }

    /// <summary>Dock-verified pallet count, when the submission carried one. Null = not verified at the yard.</summary>
    public int? VerifiedPalletCount { get; set; }
    public int? VerifiedLengthInches { get; set; }
    public int? VerifiedWidthInches { get; set; }
    public int? VerifiedHeightInches { get; set; }

    /// <summary>Serialized <see cref="YardArtifactSubmission"/> — the verbatim inspection form, for PDF regen / audit.</summary>
    public required string InspectionJson { get; set; }

    /// <summary>Serialized list of <see cref="YardArtifactFile"/> descriptors (bytes live in the file store).</summary>
    public required string FilesJson { get; set; }
}

/// <summary>A stored photo/PDF descriptor. <see cref="StoredPath"/> is relative to the file-store root.</summary>
public sealed record YardArtifactFile(
    string Id,
    YardArtifactFileKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string StoredPath);

/// <summary>Structured inspection submission (the JSON <c>payload</c> form field on the POST).</summary>
public sealed class YardArtifactSubmission
{
    public string? Yard { get; set; }
    public string? TruckUnit { get; set; }
    public string? TrailerUnit { get; set; }
    public string? LoadNumber { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>Answered Level-1 checklist items. Optional — a bare photo drop is still a valid artifact.</summary>
    public List<YardInspectionItemInput>? Items { get; set; }

    /// <summary>Dock-verified pallet count (feeds the enrichment layer with a "yard verification" source).</summary>
    public int? VerifiedPalletCount { get; set; }
    public YardVerifiedDimsInput? VerifiedDims { get; set; }

    /// <summary>Raw structured form (verbatim), preserved for PDF regeneration. Optional.</summary>
    public JsonElement? Inspection { get; set; }
}

public sealed class YardInspectionItemInput
{
    public string? Ref { get; set; }
    public string? Label { get; set; }

    /// <summary>Pass / Fail / NA (case-insensitive).</summary>
    public string? Result { get; set; }
    public string? Note { get; set; }
}

public sealed class YardVerifiedDimsInput
{
    public int? LengthInches { get; set; }
    public int? WidthInches { get; set; }
    public int? HeightInches { get; set; }
}

/// <summary>Read-model of a yard artifact for the arrivals board and load-detail surfacing.</summary>
public sealed record YardArtifactView(
    string Id,
    string Yard,
    string? TruckUnit,
    string? TrailerUnit,
    string? LoadNumber,
    string SubmittedBy,
    DateTimeOffset CapturedAt,
    DateTimeOffset CreatedAt,
    YardInspectionStatus Status,
    int PassedItems,
    int FailedItems,
    int NaItems,
    YardVerifiedPallets? VerifiedPallets,
    IReadOnlyList<YardArtifactFileView> Files);

/// <summary>
/// Dock-verified pallet count / dimensions with an explicit provenance label. <see cref="Source"/> is
/// always <c>"yard verification"</c> so the trailer-fit / enrichment layer can distinguish these from
/// EDI-tender estimates (verified dims upgrade an UNVERIFIED fit).
/// </summary>
public sealed record YardVerifiedPallets(
    int? PalletCount,
    int? LengthInches,
    int? WidthInches,
    int? HeightInches,
    string Source);

public sealed record YardArtifactFileView(
    string Id,
    YardArtifactFileKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes);

/// <summary>Query filter for surfacing artifacts by equipment/load/yard.</summary>
public sealed record YardArtifactQuery(
    string? LoadNumber = null,
    string? TruckUnit = null,
    string? TrailerUnit = null,
    string? Yard = null,
    int Max = 100);

/// <summary>A photo/PDF upload handed to the service, decoupled from ASP.NET's IFormFile.</summary>
public sealed record YardArtifactUpload(
    YardArtifactFileKind Kind,
    string FileName,
    string ContentType,
    Stream Content);

/// <summary>Thrown when a submission fails boundary validation; mapped to HTTP 400 by the controller.</summary>
public sealed class YardArtifactValidationException(string message) : Exception(message);

/// <summary>Shared mapping/serialization helpers so the store, service and tests stay consistent.</summary>
public static class YardArtifactMapping
{
    public const string VerifiedSource = "yard verification";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static YardArtifactView ToView(YardArtifactRecord record)
    {
        var files = JsonSerializer.Deserialize<List<YardArtifactFile>>(record.FilesJson, Json) ?? [];
        return new YardArtifactView(
            record.Id,
            record.Yard,
            record.TruckUnit,
            record.TrailerUnit,
            record.LoadNumber,
            record.SubmittedBy,
            record.CapturedAt,
            record.CreatedAt,
            record.Status,
            record.PassedItems,
            record.FailedItems,
            record.NaItems,
            ToVerified(record),
            files.Select(f => new YardArtifactFileView(f.Id, f.Kind, f.FileName, f.ContentType, f.SizeBytes)).ToArray());
    }

    public static IReadOnlyList<YardArtifactFile> Files(YardArtifactRecord record) =>
        JsonSerializer.Deserialize<List<YardArtifactFile>>(record.FilesJson, Json) ?? [];

    private static YardVerifiedPallets? ToVerified(YardArtifactRecord record)
    {
        if (record.VerifiedPalletCount is null
            && record.VerifiedLengthInches is null
            && record.VerifiedWidthInches is null
            && record.VerifiedHeightInches is null)
        {
            return null;
        }

        return new YardVerifiedPallets(
            record.VerifiedPalletCount,
            record.VerifiedLengthInches,
            record.VerifiedWidthInches,
            record.VerifiedHeightInches,
            VerifiedSource);
    }
}
