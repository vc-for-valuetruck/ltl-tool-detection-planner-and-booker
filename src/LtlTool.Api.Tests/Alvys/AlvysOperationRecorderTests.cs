using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Tests the operation recorder + idempotency layer over the in-memory store. The invariants:
/// every dry-run/execute produces an auditable record; an idempotency key de-duplicates equivalent
/// executes (no second executable record), conflicts on a divergent payload, and unsupported
/// operations are recorded with their unsupported status. No path ever executes a live mutation.
/// </summary>
public sealed class AlvysOperationRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private static (AlvysOperationRecorder Recorder, InMemoryAlvysOperationStore Store) Build(
        AlvysWritebackMode mode = AlvysWritebackMode.Disabled)
    {
        var write = Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions { Mode = mode });
        var alvys = Microsoft.Extensions.Options.Options.Create(new AlvysOptions());
        var gateway = new AlvysWriteGateway(write, alvys);
        var store = new InMemoryAlvysOperationStore();
        var clock = new FixedClock(Now);
        return (new AlvysOperationRecorder(gateway, store, clock, new NoOpAlvysWriteClient()), store);
    }

    private static AlvysOperationRequest Note(string key, string text = "Assigned to driver D1.") =>
        new() { LoadNumber = "L100", NoteText = text, IdempotencyKey = key };

    [Fact]
    public void DryRun_records_an_audit_entry_on_the_dry_run_channel()
    {
        var (recorder, store) = Build(AlvysWritebackMode.Simulation);

        var result = recorder.RecordDryRun("owner@vt.com", "create-load-note", Note(key: ""));

        Assert.Equal(AlvysRecordDisposition.Created, result.Disposition);
        Assert.NotNull(result.Record);
        Assert.Equal(AlvysOperationChannel.DryRun, result.Record!.Channel);
        Assert.Equal("load", result.Record.ResourceType);
        Assert.Equal("L100", result.Record.ResourceId);
        Assert.Single(store.ListForOwner("owner@vt.com", 50));
    }

    [Fact]
    public void Execute_records_an_outbox_entry_with_status_from_disposition()
    {
        var (recorder, _) = Build(AlvysWritebackMode.Disabled);

        var result = recorder.RecordExecute("owner@vt.com", "create-load-note", Note(key: ""));

        Assert.Equal(AlvysRecordDisposition.Created, result.Disposition);
        Assert.Equal(AlvysOperationChannel.Execute, result.Record!.Channel);
        // Disabled mode + unsupported operation resolves to audit-only, recorded.
        Assert.Equal(AlvysOperationRecordStatus.Recorded, result.Record.Status);
        Assert.False(result.Outcome.Executed);
        Assert.Equal(1, result.Record.AttemptCount);
    }

    [Fact]
    public void Repeated_execute_with_same_key_and_payload_replays_without_duplicating()
    {
        var (recorder, store) = Build(AlvysWritebackMode.Simulation);

        var first = recorder.RecordExecute("owner@vt.com", "create-load-note", Note("idem-1"));
        var second = recorder.RecordExecute("owner@vt.com", "create-load-note", Note("idem-1"));

        Assert.Equal(AlvysRecordDisposition.Created, first.Disposition);
        Assert.Equal(AlvysRecordDisposition.DuplicateReplay, second.Disposition);
        Assert.Equal(first.Record!.Id, second.Record!.Id);
        Assert.Equal(2, second.Record.AttemptCount);
        // Only one executable record exists for the key.
        Assert.Single(store.ListForOwner("owner@vt.com", 50));
    }

    [Fact]
    public void Same_key_with_different_payload_conflicts_and_records_nothing_new()
    {
        var (recorder, store) = Build(AlvysWritebackMode.Simulation);

        var first = recorder.RecordExecute("owner@vt.com", "create-load-note", Note("idem-2", "first text"));
        var conflict = recorder.RecordExecute("owner@vt.com", "create-load-note", Note("idem-2", "different text"));

        Assert.Equal(AlvysRecordDisposition.Conflict, conflict.Disposition);
        Assert.Null(conflict.Record);
        Assert.Equal(first.Record!.Id, conflict.ConflictingRecordId);
        Assert.Single(store.ListForOwner("owner@vt.com", 50));
    }

    [Fact]
    public void Keyless_executes_are_never_de_duplicated()
    {
        var (recorder, store) = Build(AlvysWritebackMode.Simulation);

        recorder.RecordExecute("owner@vt.com", "create-load-note", Note(key: ""));
        recorder.RecordExecute("owner@vt.com", "create-load-note", Note(key: ""));

        Assert.Equal(2, store.ListForOwner("owner@vt.com", 50).Count);
    }

    [Fact]
    public void Idempotency_is_scoped_per_owner()
    {
        var (recorder, store) = Build(AlvysWritebackMode.Simulation);

        var alice = recorder.RecordExecute("alice@vt.com", "create-load-note", Note("shared-key"));
        var bob = recorder.RecordExecute("bob@vt.com", "create-load-note", Note("shared-key"));

        // Same key, different owners: two independent records, neither a replay.
        Assert.Equal(AlvysRecordDisposition.Created, alice.Disposition);
        Assert.Equal(AlvysRecordDisposition.Created, bob.Disposition);
        Assert.Single(store.ListForOwner("alice@vt.com", 50));
        Assert.Single(store.ListForOwner("bob@vt.com", 50));
        Assert.Empty(store.ListForOwner("carol@vt.com", 50));
    }

    [Fact]
    public void Sandbox_mode_without_config_records_as_simulated()
    {
        // Sandbox mode without sandbox URL/credentials: eligible gate is not cleared, stays Simulated.
        var (recorder, _) = Build(AlvysWritebackMode.Sandbox);

        var result = recorder.RecordExecute("owner@vt.com", "create-load-note", Note("k"));

        Assert.Equal(AlvysOperationDisposition.Simulated, result.Outcome.Disposition);
        Assert.Equal(AlvysOperationRecordStatus.Recorded, result.Record!.Status);
        Assert.False(result.Outcome.Executed);
        Assert.False(result.Outcome.SandboxExecutionEligible);
    }

    [Fact]
    public void Blocked_request_is_recorded_with_blocked_status()
    {
        var (recorder, _) = Build(AlvysWritebackMode.Simulation);

        // Missing note text → validation blocks; no payload is built but the attempt is still audited.
        var result = recorder.RecordExecute("owner@vt.com", "create-load-note",
            new AlvysOperationRequest { LoadNumber = "L100", IdempotencyKey = "k" });

        Assert.Equal(AlvysOperationDisposition.Blocked, result.Outcome.Disposition);
        Assert.Equal(AlvysOperationRecordStatus.Blocked, result.Record!.Status);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
