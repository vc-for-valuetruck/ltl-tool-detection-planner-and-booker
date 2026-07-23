namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// Configuration for the Yard→LTL scheduler ingestion pipeline. Bound from the <c>YardIngestion</c>
/// section. All defaults match the v1 contract, so a fresh deployment accepts <c>yard-control</c>
/// events without extra config.
/// </summary>
public sealed class YardIngestionOptions
{
    public const string SectionName = "YardIngestion";

    /// <summary>The only schema version the v1 endpoint understands. Other versions are rejected (400).</summary>
    public int SupportedSchemaVersion { get; set; } = 1;

    /// <summary>
    /// The source system the endpoint accepts. Defaults to <c>yard-control</c> per the contract; an
    /// event whose <c>sourceSystem</c> differs is rejected (400) so this endpoint cannot be misused as
    /// a generic sink. Empty disables the check (any source accepted).
    /// </summary>
    public string ExpectedSourceSystem { get; set; } = "yard-control";

    /// <summary>
    /// Entra <b>app role</b> a caller must present to POST events. Yard's managed identity is granted
    /// this role on the LTL API app registration; its client-credentials token then carries the role
    /// in the <c>roles</c> claim. Empty disables the app-role check (any authenticated caller).
    /// </summary>
    public string RequiredAppRole { get; set; } = "YardEvents.Ingest";

    /// <summary>
    /// Alternative delegated <b>scope</b> (<c>scp</c> claim) that also satisfies the ingest policy, for
    /// callers using a delegated token rather than an app role. Empty = only the app role is accepted.
    /// </summary>
    public string? RequiredScope { get; set; }
}
