namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Phase 6 signal-extraction abstraction. Turns one piece of unstructured text (a note, email,
/// call summary, or transcript) into typed <see cref="ExtractedSignal"/>s. The default
/// implementation is deterministic keyword/dictionary matching (<see cref="KeywordSignalExtractor"/>);
/// an LLM-backed extractor is pluggable behind this interface but is NOT required to be wired to a
/// live model. Deterministic rules keep dispatch decisions explainable and testable offline.
///
/// <para><b>Fail-closed contract.</b> Unlike the accessorial extractor (which degrades to empty),
/// this extractor is allowed to signal failure by throwing <see cref="SignalExtractorException"/>.
/// The ingest service treats any throw — or any produced signal lacking a verbatim evidence quote —
/// as a hard failure: nothing is persisted and a legible error is returned. No partial writes.</para>
///
/// <para><b>Guardrail.</b> The extractor NEVER asserts numeric operational values (revenue, weight,
/// miles). It only emits typed text signals with a verbatim evidence quote drawn from the input.</para>
/// </summary>
public interface ISignalExtractor
{
    /// <summary>Human-readable name of the active extractor, surfaced for honesty in the UI/audit.</summary>
    string Name { get; }

    /// <summary>
    /// Extract typed signals from <paramref name="text"/>. May return an empty list (no signals
    /// found — a valid outcome). Throws <see cref="SignalExtractorException"/> when the extractor
    /// is unavailable or fails, so the caller can fail closed.
    /// </summary>
    Task<IReadOnlyList<ExtractedSignal>> ExtractAsync(
        string sourceType, string sourceId, string text, CancellationToken ct = default);
}

/// <summary>
/// Signals that the extractor could not run or failed mid-extraction (e.g. an LLM transport error,
/// a disabled/unconfigured backend). The ingest service converts this into a fail-closed
/// <see cref="SignalIngestException"/> — the request records nothing.
/// </summary>
public sealed class SignalExtractorException(string message, Exception? inner = null)
    : Exception(message, inner);
