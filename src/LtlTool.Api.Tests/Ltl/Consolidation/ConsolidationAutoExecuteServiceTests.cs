using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Gate + execute tests for the auto-execute orchestrator (docs/AUTO_CONSOLIDATE_SPEC.md §5, §6).
/// The single most important guarantee (§6.1): the feature is architecturally unreachable in
/// production — with the flag OFF, and (critically) even with the flag ON and the internal API fully
/// armed, the sign-off / live-support gate blocks every dispatch because all three internal
/// operations stay <see cref="AlvysLiveSupport.Unsupported"/> in Phase 1a. The dispatch-path tests
/// use the test-only live-support seam so the sequencing / halt-on-failure logic can be exercised
/// without flipping the shared static registry.
/// </summary>
public sealed class ConsolidationAutoExecuteServiceTests
{
    private static readonly DateTimeOffset Pickup = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    // ---- Gate: unreachable when the flag is off -------------------------------------------------

    [Fact]
    public async Task Gate_is_closed_when_the_feature_flag_is_off()
    {
        var recorder = new SpyRecorder();
        var service = BuildService(recorder, autoConsolidateEnabled: false, isLiveSupported: _ => true);

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.False(response.Dispatched);
        Assert.False(response.Executed);
        Assert.All(response.Steps, s =>
            Assert.Equal(ConsolidationAutoExecuteStepStatus.NotDispatched, s.Status));
        Assert.Contains(response.Blockers, b => b.Contains("disabled"));
        Assert.Equal(0, recorder.Calls); // the recorder is NEVER touched when the gate is closed.
    }

    // ---- Gate: THE sign-off gate (most important) -----------------------------------------------

    [Fact]
    public async Task Sign_off_gate_blocks_dispatch_even_when_flag_on_and_internal_api_armed()
    {
        // Flag ON + readiness enabled, but the real registry (default live-support predicate) is used:
        // every internal op is AlvysLiveSupport.Unsupported, so no operation is signed off for live
        // execution. This is the guarantee that keeps production execution architecturally unreachable.
        var recorder = new SpyRecorder();
        var service = BuildService(recorder, autoConsolidateEnabled: true, isLiveSupported: null);

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.False(response.Dispatched);
        Assert.False(response.Executed);
        Assert.Contains(response.Blockers, b => b.Contains("not signed off for live execution"));
        Assert.All(response.Steps, s =>
            Assert.Equal(ConsolidationAutoExecuteStepStatus.NotDispatched, s.Status));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Production_shaped_internal_host_forces_gate_closed_regardless_of_flag()
    {
        // Spec §6.3: a production-shaped host resolves AutoConsolidateEnabled to false even with the
        // flag on, so the orchestrator refuses to dispatch. Uses the permissive live-support seam to
        // prove the block comes from the production-host guard, not the sign-off gate.
        var recorder = new SpyRecorder();
        var service = BuildService(
            recorder,
            autoConsolidateEnabled: true,
            isLiveSupported: _ => true,
            internalBaseUrl: "https://integrations.alvys.com");

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.False(response.Dispatched);
        Assert.Contains(response.Blockers, b => b.Contains("disabled"));
        Assert.Equal(0, recorder.Calls);
    }

    // ---- Dispatch path (test seam: pretend the ops are signed off) ------------------------------

    [Fact]
    public async Task Dispatch_executes_every_step_and_reports_executed_when_all_confirm()
    {
        var recorder = new SpyRecorder(); // defaults to confirmed for every call
        var service = BuildService(recorder, autoConsolidateEnabled: true, isLiveSupported: _ => true);

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.True(response.Dispatched);
        Assert.True(response.Executed);
        Assert.Empty(response.Blockers);
        // 1 parent set-refs + 1 waypoint + 1 zero-child + 1 child set-refs = 4.
        Assert.Equal(4, recorder.Calls);
        Assert.All(response.Steps, s =>
            Assert.Equal(ConsolidationAutoExecuteStepStatus.Confirmed, s.Status));
    }

    [Fact]
    public async Task Dispatch_halts_on_first_failure_and_skips_the_rest_no_rollback()
    {
        // Spec §5: halt-on-first-failure, no auto-rollback. The second call fails; every later step is
        // Skipped (not executed) and the already-executed first step is NOT rolled back.
        var recorder = new SpyRecorder { FailOnCall = 2 };
        var service = BuildService(recorder, autoConsolidateEnabled: true, isLiveSupported: _ => true);

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.True(response.Dispatched);
        Assert.False(response.Executed);
        Assert.Equal(2, recorder.Calls); // stopped after the failure — no further dispatch.

        Assert.Equal(ConsolidationAutoExecuteStepStatus.Confirmed, response.Steps[0].Status);
        Assert.Equal(ConsolidationAutoExecuteStepStatus.Failed, response.Steps[1].Status);
        Assert.All(response.Steps.Skip(2), s =>
            Assert.Equal(ConsolidationAutoExecuteStepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Dispatch_halts_on_conflict()
    {
        var recorder = new SpyRecorder { ConflictOnCall = 1 };
        var service = BuildService(recorder, autoConsolidateEnabled: true, isLiveSupported: _ => true);

        var response = await service.ExecuteAsync(ValidRequest(), "owner@vt.com");

        Assert.False(response.Executed);
        Assert.Equal(ConsolidationAutoExecuteStepStatus.Conflict, response.Steps[0].Status);
        Assert.All(response.Steps.Skip(1), s =>
            Assert.Equal(ConsolidationAutoExecuteStepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Sibling_not_in_resolved_plan_is_refused_before_any_dispatch()
    {
        // Spec §9: writes stay corridor-bounded. A sibling the read-only plan did not resolve is
        // refused, the gate closes, and nothing dispatches.
        var recorder = new SpyRecorder();
        var service = BuildService(recorder, autoConsolidateEnabled: true, isLiveSupported: _ => true);

        var request = ValidRequest();
        request.Siblings.Add(new ConsolidationAutoExecuteSibling
        {
            LoadId = "L-NOT-IN-PLAN",
            ChildTripId = "T-999",
            CompanyId = "C-999",
            Sequence = 9,
        });

        var response = await service.ExecuteAsync(request, "owner@vt.com");

        Assert.False(response.Dispatched);
        Assert.Contains(response.Blockers, b => b.Contains("L-NOT-IN-PLAN") && b.Contains("not part of"));
        Assert.Equal(0, recorder.Calls);
    }

    // ---- Session status -------------------------------------------------------------------------

    [Fact]
    public async Task Session_status_reports_disabled_when_the_flag_is_off()
    {
        var service = BuildService(new SpyRecorder(), autoConsolidateEnabled: false, isLiveSupported: _ => true);

        var status = await service.GetSessionStatusAsync("dispatcher-1");

        Assert.False(status.AutoConsolidateEnabled);
        Assert.False(status.HasValidSession);
    }

    [Fact]
    public async Task Session_status_reports_no_session_when_token_acquisition_fails()
    {
        var service = BuildService(
            new SpyRecorder(), autoConsolidateEnabled: true, isLiveSupported: _ => true,
            tokenProvider: new ThrowingTokenProvider("no configured internal session"));

        var status = await service.GetSessionStatusAsync("dispatcher-1");

        Assert.True(status.AutoConsolidateEnabled);
        Assert.False(status.HasValidSession);
        Assert.Equal("no configured internal session", status.Reason);
        Assert.Null(status.ExpiresInSeconds); // never introspected, token never returned.
    }

    [Fact]
    public async Task Session_status_reports_valid_when_a_token_is_acquired()
    {
        var service = BuildService(
            new SpyRecorder(), autoConsolidateEnabled: true, isLiveSupported: _ => true,
            tokenProvider: new StubTokenProvider("session-token-value"));

        var status = await service.GetSessionStatusAsync("dispatcher-1");

        Assert.True(status.HasValidSession);
        Assert.Null(status.ExpiresInSeconds);
    }

    // ---- Wiring ---------------------------------------------------------------------------------

    private static ConsolidationAutoExecuteRequest ValidRequest() => new()
    {
        Plan = new ConsolidationPlanRequest
        {
            ParentLoadId = "L-100234",
            SiblingLoadIds = ["L-100241"],
        },
        ParentTripId = "T-100234",
        ParentLoadNumber = "L-100234",
        ActingUserId = "dispatcher-1",
        Reason = "pilot consolidation",
        Siblings =
        [
            new ConsolidationAutoExecuteSibling
            {
                LoadId = "L-100241",
                LoadNumber = "L-100241",
                ChildTripId = "T-100241",
                CompanyId = "C-500",
                Sequence = 1,
            },
        ],
    };

    private static ConsolidationAutoExecuteService BuildService(
        SpyRecorder recorder,
        bool autoConsolidateEnabled,
        Func<AlvysWriteOperationDescriptor?, bool>? isLiveSupported,
        string? internalBaseUrl = null,
        IAlvysInternalTokenProvider? tokenProvider = null)
    {
        var plans = BuildPlanService();
        var readiness = BuildReadiness(autoConsolidateEnabled, internalBaseUrl);
        var options = Options.Create(new ConsolidationAutoExecuteOptions { Enabled = autoConsolidateEnabled });
        var logger = NullLogger<ConsolidationAutoExecuteService>.Instance;
        tokenProvider ??= new StubTokenProvider("t");

        // When isLiveSupported is null, exercise the production DI constructor (real registry default),
        // which is what enforces the Unsupported sign-off gate.
        return isLiveSupported is null
            ? new ConsolidationAutoExecuteService(plans, readiness, recorder, tokenProvider, options, logger)
            : new ConsolidationAutoExecuteService(
                plans, readiness, recorder, tokenProvider, options, logger, isLiveSupported);
    }

    private static IAlvysReadinessService BuildReadiness(bool autoConsolidateEnabled, string? internalBaseUrl)
    {
        var internalOptions = new AlvysInternalApiOptions();
        if (!string.IsNullOrWhiteSpace(internalBaseUrl))
        {
            internalOptions.Enabled = true;
            internalOptions.BaseUrl = internalBaseUrl;
        }

        return new AlvysReadinessService(
            Options.Create(new AlvysWriteOptions()),
            Options.Create(new AlvysOptions()),
            new InMemoryAlvysSyncTracker(),
            Options.Create(internalOptions),
            Options.Create(new ConsolidationAutoExecuteOptions { Enabled = autoConsolidateEnabled }));
    }

    private static ConsolidationPlanService BuildPlanService()
    {
        var parent = Load("L-100234", "Verdef", Pickup);
        var sibling = Load("L-100241", "Verdef", Pickup.AddHours(3));
        var client = new StatefulAlvysClient(parent, sibling);

        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());

        var options = new ConsolidationOptions();
        options.CustomerPolicies.Add(new() { Customer = "Verdef", Tier = CustomerConsolidationTier.Allowed });

        return new ConsolidationPlanService(
            loads,
            Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new NullStopSequencer(LtlTestFactory.Clock()));
    }

    private static AlvysLoad Load(string id, string customer, DateTimeOffset pickup) => new()
    {
        Id = id,
        LoadNumber = id,
        Status = "Available",
        CustomerName = customer,
        CustomerRate = 4100m,
        CustomerMileage = 500m,
        Weight = 4100m,
        Stops =
        [
            new AlvysLoadStop
            {
                StopType = "Pickup",
                Address = new AlvysAddress { City = "Laredo", State = "TX" },
                ScheduledStart = pickup,
                Sequence = 1,
            },
            new AlvysLoadStop
            {
                StopType = "Delivery",
                Address = new AlvysAddress { City = "Dallas", State = "TX" },
                ScheduledStart = pickup.AddDays(1),
                Sequence = 2,
            },
        ],
    };

    // ---- Test doubles ---------------------------------------------------------------------------

    /// <summary>
    /// Records how many times the recorder execute path is hit and returns a configurable disposition
    /// per call (confirmed by default; a specific call can be made to fail or conflict).
    /// </summary>
    private sealed class SpyRecorder : IAlvysOperationRecorder
    {
        public int Calls { get; private set; }
        public int? FailOnCall { get; init; }
        public int? ConflictOnCall { get; init; }

        public AlvysRecordResult RecordDryRun(string ownerId, string operationCode, AlvysOperationRequest request)
            => throw new NotImplementedException();

        public AlvysRecordResult RecordExecute(string ownerId, string operationCode, AlvysOperationRequest request)
            => throw new NotImplementedException();

        public Task<AlvysRecordResult> RecordExecuteAsync(
            string ownerId, string operationCode, AlvysOperationRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (ConflictOnCall == Calls)
                return Task.FromResult(Conflict(operationCode));
            if (FailOnCall == Calls)
                return Task.FromResult(Failed(operationCode));
            return Task.FromResult(Confirmed(operationCode));
        }

        private static AlvysRecordResult Confirmed(string code) => new()
        {
            Disposition = AlvysRecordDisposition.Created,
            Outcome = Outcome(code, AlvysOperationDisposition.InternalExecuted, executed: true),
            Record = Record(code),
        };

        private static AlvysRecordResult Failed(string code) => new()
        {
            Disposition = AlvysRecordDisposition.Created,
            Outcome = Outcome(code, AlvysOperationDisposition.InternalFailed, executed: false),
            Record = Record(code),
        };

        private static AlvysRecordResult Conflict(string code) => new()
        {
            Disposition = AlvysRecordDisposition.Conflict,
            Outcome = Outcome(code, AlvysOperationDisposition.InternalExecuted, executed: false),
            Record = null,
            ConflictingRecordId = "existing-1",
        };

        private static AlvysOperationOutcome Outcome(
            string code, AlvysOperationDisposition disposition, bool executed) => new()
        {
            OperationCode = code,
            Title = code,
            Mode = AlvysWritebackMode.Sandbox,
            Disposition = disposition,
            Executed = executed,
            Message = disposition.ToString(),
        };

        private static AlvysOperationRecord Record(string code) => new()
        {
            Id = $"rec-{Guid.NewGuid():n}",
            OwnerId = "owner@vt.com",
            OperationCode = code,
            PayloadHash = "hash",
        };
    }

    private sealed class StubTokenProvider(string token) : IAlvysInternalTokenProvider
    {
        public Task<string> GetSessionTokenAsync(string actingUserId, CancellationToken ct = default)
            => Task.FromResult(token);

        public void InvalidateToken(string actingUserId) { }
    }

    private sealed class ThrowingTokenProvider(string message) : IAlvysInternalTokenProvider
    {
        public Task<string> GetSessionTokenAsync(string actingUserId, CancellationToken ct = default)
            => throw new InvalidOperationException(message);

        public void InvalidateToken(string actingUserId) { }
    }
}
