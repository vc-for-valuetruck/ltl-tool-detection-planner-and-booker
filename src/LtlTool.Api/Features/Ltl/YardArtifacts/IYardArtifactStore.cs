namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Durable metadata store for yard artifacts. Internal data only — nothing here reads from or writes
/// to Alvys. The production implementation is <see cref="EfYardArtifactStore"/> (AppDbContext / SQL
/// Server); tests use an in-memory double.
/// </summary>
public interface IYardArtifactStore
{
    void Add(YardArtifactRecord record);

    YardArtifactRecord? Get(string id);

    IReadOnlyList<YardArtifactRecord> Query(YardArtifactQuery query);
}
