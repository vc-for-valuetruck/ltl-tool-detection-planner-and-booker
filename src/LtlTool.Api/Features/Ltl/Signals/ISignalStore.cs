namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Durable store for extracted LTL signals. Internal data — nothing here reads from or writes to
/// Alvys. Ingestion is atomic: a batch of signals from one request is persisted in a single
/// operation so a failure never leaves a partial write.
/// </summary>
public interface ISignalStore
{
    /// <summary>
    /// Persist a batch of signals atomically. Either all rows are saved or none are (fail-closed).
    /// </summary>
    void AddBatch(IReadOnlyList<SignalRecord> records);

    SignalRecord? Get(string id);

    IReadOnlyList<SignalRecord> Query(SignalQuery query);

    /// <summary>
    /// Transition a signal to Accepted/Rejected with the deciding user + timestamp. Returns the
    /// updated record, or null when the id is unknown. Never touches Alvys.
    /// </summary>
    SignalRecord? UpdateStatus(string id, SignalStatus status, string decidedBy, DateTimeOffset decidedAt);
}
