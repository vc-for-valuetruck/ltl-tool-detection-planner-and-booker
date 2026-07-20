namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Persists yard-artifact photo/PDF bytes outside SQL (local disk today; a mounted volume or blob
/// path in production). The store only ever holds the relative descriptor returned here. Internal
/// data — never Alvys.
/// </summary>
public interface IYardArtifactFileStore
{
    Task<YardArtifactFile> SaveAsync(
        string artifactId,
        YardArtifactFileKind kind,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken ct);

    /// <summary>Opens a stored file for streaming back, or null when the bytes are missing.</summary>
    YardArtifactStreamedFile? Open(YardArtifactFile file);
}

public sealed record YardArtifactStreamedFile(Stream Content, string ContentType, string FileName);
