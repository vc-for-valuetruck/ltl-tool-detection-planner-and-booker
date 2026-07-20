namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Configuration for yard-artifact intake. Bound from <c>Ltl:YardArtifacts</c>. All values have safe
/// defaults so a fresh clone / CI / the demo work without configuration — photos land under the app's
/// content root and the size/count limits are enforced at the boundary.
/// </summary>
public sealed class YardArtifactOptions
{
    public const string SectionName = "Ltl:YardArtifacts";

    /// <summary>
    /// Root directory for stored photo/PDF bytes. When null/empty the file store resolves it to
    /// <c>{ContentRoot}/App_Data/yard-artifacts</c>. In production point this at a mounted volume /
    /// blob-backed path; the SQL row only ever holds the relative descriptor, never the bytes.
    /// </summary>
    public string? StorageRoot { get; set; }

    /// <summary>Per-file ceiling. Anything larger is rejected with HTTP 400 rather than stored.</summary>
    public long MaxFileBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Maximum number of files (photos + PDF) accepted on a single submission.</summary>
    public int MaxFiles { get; set; } = 60;
}
