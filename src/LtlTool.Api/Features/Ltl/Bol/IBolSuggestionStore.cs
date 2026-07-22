namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Durable store for suggested BOL fields awaiting human review. Internal data — nothing here reads
/// from or writes to Alvys. A read is atomic: the batch of suggestions from one document is persisted
/// in a single operation so a failure never leaves a partial set of suggestions.
/// </summary>
public interface IBolSuggestionStore
{
    /// <summary>
    /// Persist a batch of suggestions atomically. Either all rows are saved or none are (fail-closed).
    /// </summary>
    void AddBatch(IReadOnlyList<BolFieldSuggestionRecord> records);

    BolFieldSuggestionRecord? Get(string id);

    IReadOnlyList<BolFieldSuggestionRecord> Query(BolSuggestionQuery query);

    /// <summary>
    /// Transition a suggestion to Accepted/Rejected with the deciding user + timestamp. Returns the
    /// updated record, or null when the id is unknown. Never touches Alvys.
    /// </summary>
    BolFieldSuggestionRecord? UpdateStatus(
        string id, BolSuggestionStatus status, string decidedBy, DateTimeOffset decidedAt);
}
