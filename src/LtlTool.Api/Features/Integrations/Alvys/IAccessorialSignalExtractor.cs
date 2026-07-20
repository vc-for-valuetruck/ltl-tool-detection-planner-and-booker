using LtlTool.Api.Features.Ltl;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Phase 6 AI signal-extraction layer: extract accessorial signals from unstructured text sourced
/// from Alvys notes/documents. The implementation is selected at startup by
/// <c>Ltl:AccessorialAi:Enabled</c> — when disabled the <see cref="NullAccessorialSignalExtractor"/>
/// is registered and no network/LLM call is ever made.
///
/// <para>
/// This interface is intentionally separate from the deterministic
/// <c>AccessorialSignalAnalyzer</c> (keyword extraction) so the AI layer is mockable, testable
/// without credentials, and replaceable without touching the deterministic rules.
/// </para>
///
/// <para>Guardrail: the extractor NEVER prices, NEVER asserts dollar amounts, and NEVER
/// invents evidence. It only extracts evidence quotes that are present in the supplied text.
/// </para>
/// </summary>
public interface IAccessorialSignalExtractor
{
    /// <summary>True when the extractor will actually make network/LLM calls; false for the null implementation.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Extract accessorial signals from a single piece of unstructured text. Returns an empty
    /// list when the extractor is disabled, the text is blank, or no signals are found.
    /// Never throws: degrade to empty on any LLM failure so the deterministic signals still surface.
    /// </summary>
    Task<IReadOnlyList<AccessorialSignal>> ExtractAsync(
        string sourceId, string sourceType, string text, CancellationToken ct = default);
}

/// <summary>
/// No-op <see cref="IAccessorialSignalExtractor"/> used when <c>Ltl:AccessorialAi:Enabled = false</c>
/// (the default). Registered as a singleton so the analyzer always has a safe fallback — no network
/// call is ever made.
/// </summary>
public sealed class NullAccessorialSignalExtractor : IAccessorialSignalExtractor
{
    public bool IsEnabled => false;

    public Task<IReadOnlyList<AccessorialSignal>> ExtractAsync(
        string sourceId, string sourceType, string text, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccessorialSignal>>([]);
}
