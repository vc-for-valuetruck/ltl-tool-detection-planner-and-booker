namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Turns the plain text of a BOL into suggested, evidence-backed fields. The default implementation
/// (<see cref="RegexBolFieldExtractor"/>) is deterministic regex/keyword matching — no LLM, no network
/// call — so every suggestion is reproducible from the input and explainable to a human reviewer.
///
/// <para><b>Fail-closed contract.</b> The extractor throws <see cref="BolFieldExtractionException"/>
/// when it cannot run. The read service treats any throw — or any suggested field lacking a verbatim
/// evidence quote drawn from the source text — as a hard failure: nothing is persisted and a legible
/// error is returned. No partial suggestions.</para>
///
/// <para><b>Guardrail.</b> A suggestion is exactly that — it is NEVER auto-applied and NEVER written
/// to Alvys. A human accepts each field in the UI; only then does it annotate internal surfaces, with
/// a full audit (who accepted, when, from which document).</para>
/// </summary>
public interface IBolFieldExtractor
{
    /// <summary>Human-readable name of the active extractor, surfaced for honesty in the UI/audit.</summary>
    string Name { get; }

    /// <summary>
    /// Extract suggested fields from <paramref name="text"/>. May return an empty list (nothing
    /// confidently found — a valid outcome). Throws <see cref="BolFieldExtractionException"/> when the
    /// extractor is unavailable or fails, so the caller can fail closed.
    /// </summary>
    IReadOnlyList<ExtractedBolField> Extract(string text);
}

/// <summary>
/// Signals that the field extractor could not run or failed mid-extraction. The read service converts
/// this into a fail-closed <see cref="BolReadException"/> — the read records nothing.
/// </summary>
public sealed class BolFieldExtractionException(string message, Exception? inner = null)
    : Exception(message, inner);
