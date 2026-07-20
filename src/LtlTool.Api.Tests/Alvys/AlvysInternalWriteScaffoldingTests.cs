using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// End-to-end scaffolding tests for the Phase-2 internal-API write path (decision #10). Covers the
/// gateway gate + validation + payload construction for the three internal operations, the snapshot
/// regression harness that fails loudly if a call-site payload shape drifts, the recorder routing an
/// internal-eligible execute through the internal write client, the mandated token_expired →
/// InternalFailed outcome, and the readiness surface excluding internal operations.
/// </summary>
public sealed class AlvysInternalWriteScaffoldingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Alvys", "Fixtures", "internal");

    // ---- Gateway gate + validation --------------------------------------------------------------

    [Fact]
    public void Internal_operation_is_audit_only_when_the_internal_api_is_disabled()
    {
        var gateway = BuildGateway(internalOptions: new AlvysInternalApiOptions()); // Enabled=false
        var outcome = gateway.Execute("set-trip-references", SetRefsRequest());

        Assert.Equal(AlvysOperationDisposition.AuditOnly, outcome.Disposition);
        Assert.False(outcome.InternalExecutionEligible);
        Assert.Contains(outcome.Blockers, b => b.Contains("disabled"));
    }

    [Fact]
    public void Internal_operation_is_audit_only_when_enabled_but_not_armed()
    {
        var gateway = BuildGateway(internalOptions: new AlvysInternalApiOptions
        {
            Enabled = true,
            BaseUrl = "https://internal.example.com",
            // EnableSetTripReferences left false → not armed.
        });
        var outcome = gateway.Execute("set-trip-references", SetRefsRequest());

        Assert.Equal(AlvysOperationDisposition.AuditOnly, outcome.Disposition);
        Assert.False(outcome.InternalExecutionEligible);
        Assert.Contains(outcome.Blockers, b => b.Contains("not individually armed"));
    }

    [Fact]
    public void Internal_operation_is_eligible_when_enabled_armed_and_executing()
    {
        var gateway = BuildGateway(internalOptions: ArmedFor(x => x.EnableSetTripReferences = true));
        var outcome = gateway.Execute("set-trip-references", SetRefsRequest());

        Assert.Equal(AlvysOperationDisposition.InternalExecuted, outcome.Disposition);
        Assert.True(outcome.InternalExecutionEligible);
        Assert.Empty(outcome.Blockers);
    }

    [Fact]
    public void Internal_dry_run_is_simulated_even_when_armed()
    {
        var gateway = BuildGateway(internalOptions: ArmedFor(x => x.EnableSetTripReferences = true));
        var outcome = gateway.DryRun("set-trip-references", SetRefsRequest());

        Assert.Equal(AlvysOperationDisposition.Simulated, outcome.Disposition);
        Assert.False(outcome.InternalExecutionEligible);
    }

    [Fact]
    public void Set_trip_references_requires_at_least_one_reference()
    {
        var gateway = BuildGateway(internalOptions: ArmedFor(x => x.EnableSetTripReferences = true));
        var outcome = gateway.Execute("set-trip-references",
            new AlvysOperationRequest { TripId = "T-1", ActingUserId = "u1" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, v => v.Code == "REFERENCE_REQUIRED");
    }

    [Fact]
    public void Internal_operations_require_an_acting_user()
    {
        var gateway = BuildGateway(internalOptions: ArmedFor(x => x.EnableZeroChildDispatchMiles = true));
        var outcome = gateway.Execute("zero-child-dispatch-miles",
            new AlvysOperationRequest { TripId = "T-1" }); // no ActingUserId

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, v => v.Code == "ACTING_USER_REQUIRED");
    }

    [Fact]
    public void Add_extended_stop_requires_a_waypoint()
    {
        var gateway = BuildGateway(internalOptions: ArmedFor(x => x.EnableAddExtendedStop = true));
        var outcome = gateway.Execute("add-extended-stop",
            new AlvysOperationRequest { TripId = "T-1", ActingUserId = "u1" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, v => v.Code == "WAYPOINT_REQUIRED");
    }

    // ---- Snapshot regression (observed-not-contracted call sites) --------------------------------

    [Theory]
    [InlineData("add-extended-stop")]
    [InlineData("zero-child-dispatch-miles")]
    [InlineData("set-trip-references")]
    public void Payload_body_matches_the_recorded_snapshot(string operation)
    {
        var gateway = BuildGateway(internalOptions: FullyArmed());
        var outcome = gateway.DryRun(operation, RequestFor(operation));

        Assert.NotNull(outcome.Payload);
        var actual = Canonicalize(JsonSerializer.Serialize(outcome.Payload!.Body));
        var expected = Canonicalize(File.ReadAllText(Path.Combine(FixtureDir, $"{operation}.request.json")));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Session_token_response_snapshot_still_deserializes()
    {
        // Fails loudly if the recorded session-token response shape drifts from the wire model.
        var json = File.ReadAllText(Path.Combine(FixtureDir, "session-token.response.json"));
        var parsed = JsonSerializer.Deserialize<AlvysInternalSessionTokenResponse>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(parsed);
        Assert.False(string.IsNullOrWhiteSpace(parsed!.AccessToken));
        Assert.True(parsed.ExpiresIn > 0);
    }

    // ---- Recorder routing -----------------------------------------------------------------------

    [Fact]
    public async Task Internal_eligible_execute_dispatches_and_reports_internal_executed()
    {
        var (recorder, store, client) = BuildRecorder();

        var result = await recorder.RecordExecuteAsync("owner@vt.com", "set-trip-references", SetRefsRequest("k1"));

        Assert.Equal(1, client.Calls);
        Assert.Equal(AlvysOperationDisposition.InternalExecuted, result.Outcome.Disposition);
        Assert.True(result.Outcome.Executed);
        Assert.Equal(AlvysOperationRecordStatus.Recorded, result.Record!.Status);
        _ = store;
    }

    [Fact]
    public async Task Internal_write_failure_marks_the_outbox_internal_failed()
    {
        var (recorder, _, client) = BuildRecorder();
        client.Result = new AlvysWriteCallResult { IsSuccess = false, StatusCode = 502, Error = "HTTP 502" };

        var result = await recorder.RecordExecuteAsync("owner@vt.com", "set-trip-references", SetRefsRequest("k2"));

        Assert.Equal(AlvysOperationDisposition.InternalFailed, result.Outcome.Disposition);
        Assert.False(result.Outcome.Executed);
        Assert.Equal(AlvysOperationRecordStatus.InternalFailed, result.Record!.Status);
        Assert.Equal("HTTP 502", result.Record.LastError);
    }

    [Fact]
    public async Task Token_expired_end_to_end_marks_the_outbox_internal_failed()
    {
        // Recorder → real internal write client → fake handler that always returns token_expired.
        // The client retries exactly once, then surfaces an honest failure; the recorder marks the
        // outbox row InternalFailed (never a false success). This is the mandated end-to-end path.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"token_expired"}"""),
        });
        var options = FullyArmed();
        var tokenProvider = new AlvysInternalTokenProvider(
            new StubHttpClientFactory(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"accessToken":"s","expiresIn":3600}"""),
            })),
            Microsoft.Extensions.Options.Options.Create(options),
            new CapturingLogger<AlvysInternalTokenProvider>());
        var writeClient = new AlvysHttpInternalWriteClient(
            new StubHttpClientFactory(handler), tokenProvider,
            Microsoft.Extensions.Options.Options.Create(options),
            new CapturingLogger<AlvysHttpInternalWriteClient>());

        var gateway = BuildGateway(internalOptions: options);
        var store = new InMemoryAlvysOperationStore();
        var recorder = new AlvysOperationRecorder(
            gateway, store, new FixedClock(Now), new NoOpAlvysWriteClient(), writeClient);

        var result = await recorder.RecordExecuteAsync("owner@vt.com", "set-trip-references", SetRefsRequest("k3"));

        Assert.Equal(AlvysOperationDisposition.InternalFailed, result.Outcome.Disposition);
        Assert.Equal(AlvysOperationRecordStatus.InternalFailed, result.Record!.Status);
        Assert.Contains("token_expired", result.Record.LastError);
        Assert.Equal(2, handler.Calls.Count); // original + exactly one retry
    }

    // ---- Readiness ------------------------------------------------------------------------------

    [Fact]
    public void Readiness_surface_excludes_internal_operations()
    {
        var readiness = new AlvysReadinessService(
            Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions()),
            Microsoft.Extensions.Options.Options.Create(new AlvysOptions()),
            new InMemoryAlvysSyncTracker());

        var codes = readiness.GetStatus().Operations.Select(o => o.Code).ToHashSet();

        Assert.DoesNotContain("add-extended-stop", codes);
        Assert.DoesNotContain("zero-child-dispatch-miles", codes);
        Assert.DoesNotContain("set-trip-references", codes);
        Assert.Contains("create-load-note", codes); // Public-API operations remain.
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static AlvysWriteGateway BuildGateway(AlvysInternalApiOptions internalOptions) => new(
        Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions()),
        Microsoft.Extensions.Options.Options.Create(new AlvysOptions()),
        Microsoft.Extensions.Options.Options.Create(internalOptions));

    private static (AlvysOperationRecorder Recorder, InMemoryAlvysOperationStore Store, FakeInternalWriteClient Client)
        BuildRecorder()
    {
        var gateway = BuildGateway(FullyArmed());
        var store = new InMemoryAlvysOperationStore();
        var client = new FakeInternalWriteClient();
        var recorder = new AlvysOperationRecorder(
            gateway, store, new FixedClock(Now), new NoOpAlvysWriteClient(), client);
        return (recorder, store, client);
    }

    private static AlvysInternalApiOptions ArmedFor(Action<AlvysInternalApiOptions> arm)
    {
        var options = new AlvysInternalApiOptions
        {
            Enabled = true,
            BaseUrl = "https://internal.example.com",
        };
        arm(options);
        return options;
    }

    private static AlvysInternalApiOptions FullyArmed() => ArmedFor(x =>
    {
        x.EnableAddExtendedStop = true;
        x.EnableZeroChildDispatchMiles = true;
        x.EnableSetTripReferences = true;
    });

    private static AlvysOperationRequest SetRefsRequest(string? key = null) => new()
    {
        TripId = "T-1",
        ActingUserId = "dispatcher-1",
        LtlReference = true,
        MainLoadId = "ML-9000",
        IdempotencyKey = key,
    };

    private static AlvysOperationRequest RequestFor(string operation) => operation switch
    {
        "add-extended-stop" => new AlvysOperationRequest
        {
            TripId = "T-PARENT",
            ActingUserId = "dispatcher-1",
            WaypointStop = new AlvysWaypointStop { CompanyId = "C-500", Sequence = 2 },
        },
        "zero-child-dispatch-miles" => new AlvysOperationRequest
        {
            TripId = "T-CHILD",
            ActingUserId = "dispatcher-1",
        },
        "set-trip-references" => SetRefsRequest(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    /// <summary>Recursively sorts object keys and serializes compactly so property order never affects equality.</summary>
    private static string Canonicalize(string json)
    {
        var node = JsonNode.Parse(json);
        return Sort(node)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sorted[kv.Key] = Sort(kv.Value?.DeepClone());
                return sorted;
            case JsonArray arr:
                var newArr = new JsonArray();
                foreach (var item in arr)
                    newArr.Add(Sort(item?.DeepClone()));
                return newArr;
            default:
                return node?.DeepClone();
        }
    }

    private sealed class FakeInternalWriteClient : IAlvysInternalWriteClient
    {
        public AlvysWriteCallResult Result { get; set; } = new() { IsSuccess = true, StatusCode = 200 };
        public int Calls { get; private set; }

        public Task<AlvysWriteCallResult> ExecuteAsync(
            AlvysWriteOperationDescriptor op, AlvysOperationRequest request,
            AlvysOperationPayload payload, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
