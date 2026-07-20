using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Local-disk <see cref="IYardArtifactFileStore"/>. Writes bytes under a per-artifact folder inside the
/// configured storage root; the returned <see cref="YardArtifactFile.StoredPath"/> is relative to that
/// root. File names are generated (GUID + sanitized original), so a hostile file name can never escape
/// the root, and <see cref="Open"/> re-validates that the resolved path stays inside the root before
/// streaming. Suitable for the pilot / single-node demo; swap the implementation for blob storage in a
/// multi-node deployment without touching callers.
/// </summary>
public sealed class LocalYardArtifactFileStore(IOptions<YardArtifactOptions> options, IHostEnvironment env)
    : IYardArtifactFileStore
{
    private readonly string _root = ResolveRoot(options.Value, env);

    public async Task<YardArtifactFile> SaveAsync(
        string artifactId,
        YardArtifactFileKind kind,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken ct)
    {
        var fileId = Guid.NewGuid().ToString("n");
        var safeName = Sanitize(fileName);
        var relativePath = Path.Combine(artifactId, $"{fileId}_{safeName}");
        var fullPath = Path.Combine(_root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        long size;
        await using (var target = File.Create(fullPath))
        {
            await content.CopyToAsync(target, ct);
            size = target.Length;
        }

        return new YardArtifactFile(
            fileId,
            kind,
            safeName,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            size,
            // Store with forward slashes so the descriptor is stable across OSes.
            relativePath.Replace('\\', '/'));
    }

    public YardArtifactStreamedFile? Open(YardArtifactFile file)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, file.StoredPath));
        var rootFull = Path.GetFullPath(_root);

        // Defense in depth: refuse to read anything resolving outside the storage root.
        if (!fullPath.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(fullPath))
        {
            return null;
        }

        Stream stream = File.OpenRead(fullPath);
        return new YardArtifactStreamedFile(stream, file.ContentType, file.FileName);
    }

    private static string ResolveRoot(YardArtifactOptions options, IHostEnvironment env)
    {
        var root = string.IsNullOrWhiteSpace(options.StorageRoot)
            ? Path.Combine(env.ContentRootPath, "App_Data", "yard-artifacts")
            : options.StorageRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Sanitize(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name)) return "file";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Length > 120 ? name[^120..] : name;
    }
}
