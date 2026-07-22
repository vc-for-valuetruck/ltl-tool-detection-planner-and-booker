using LtlTool.Api.Features.Ai.Narrative;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ai.Narrative;

/// <summary>
/// Behavioral tests for <see cref="NarrativeService"/> using hand-rolled fakes (the repo does not
/// use a mocking library). Covers the kill switch, cache hit/miss, fail-closed on model exceptions,
/// malformed/incomplete model output, and the happy path. No network call is ever made.
/// </summary>
public sealed class NarrativeServiceTests
{
    private const string ValidModelJson =
        "{\"whyReview\":\"Two loads share the Laredo->Dallas corridor.\"," +
        "\"whatToVerify\":\"Confirm the combined driver RPM clears the floor.\"," +
        "\"nextAction\":\"Assign one driver and paste the click card.\"," +
        "\"citations\":[\"combinedRevenuePerMile\",\"blockers\"]}";

    [Fact]
    public async Task Kill_switch_off_returns_null_and_not_cached_without_touching_model()
    {
        var chat = new FakeChatClient(ValidModelJson);
        var planSource = new FakePlanSource(SamplePayload());
        var service = Build(enabled: false, chat, planSource);

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.Null(response);
        Assert.False(cached);
        Assert.Equal(0, chat.CallCount);
        Assert.Equal(0, planSource.CallCount);
    }

    [Fact]
    public async Task Cache_hit_returns_cached_response_and_does_not_call_model_again()
    {
        var chat = new FakeChatClient(ValidModelJson);
        var planSource = new FakePlanSource(SamplePayload());
        var service = Build(enabled: true, chat, planSource);

        var first = await service.GenerateAsync("plan-1", CancellationToken.None);
        var second = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.NotNull(first.Response);
        Assert.False(first.Cached);

        Assert.NotNull(second.Response);
        Assert.True(second.Cached);
        Assert.Equal(1, chat.CallCount); // second served from cache
        Assert.Equal(first.Response!.WhyReview, second.Response!.WhyReview);
    }

    [Fact]
    public async Task Cache_miss_on_different_plan_hash_triggers_new_model_call()
    {
        var chat = new FakeChatClient(ValidModelJson);
        var planSource = new FakePlanSource(SamplePayload());
        var service = Build(enabled: true, chat, planSource);

        var first = await service.GenerateAsync("plan-1", CancellationToken.None);

        // Same plan id, but the plan content changed → different hash → cache miss → new call.
        planSource.Payload = SamplePayload(corridor: "HOUSTON_TO_DALLAS");
        var second = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.NotNull(first.Response);
        Assert.NotNull(second.Response);
        Assert.False(second.Cached);
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task Model_exception_fails_closed_to_null_true_without_bubbling()
    {
        // Plan resolved, model threw → AI failure discriminator (null, true) → endpoint 503.
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("boom"));
        var planSource = new FakePlanSource(SamplePayload());
        var service = Build(enabled: true, chat, planSource);

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.Null(response);
        Assert.True(cached); // discriminator: AI failure, NOT "served from cache"
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task Malformed_json_returns_null_true()
    {
        var chat = new FakeChatClient("this is not json");
        var service = Build(enabled: true, chat, new FakePlanSource(SamplePayload()));

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.Null(response);
        Assert.True(cached); // AI failure → 503
    }

    [Fact]
    public async Task Missing_required_field_returns_null_true()
    {
        // nextAction absent → incomplete → null (no partial population), AI-failure discriminator.
        const string incomplete =
            "{\"whyReview\":\"x\",\"whatToVerify\":\"y\",\"citations\":[\"blockers\"]}";
        var chat = new FakeChatClient(incomplete);
        var service = Build(enabled: true, chat, new FakePlanSource(SamplePayload()));

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.Null(response);
        Assert.True(cached);
    }

    [Fact]
    public async Task Empty_citations_returns_null_true()
    {
        const string noCitations =
            "{\"whyReview\":\"x\",\"whatToVerify\":\"y\",\"nextAction\":\"z\",\"citations\":[]}";
        var chat = new FakeChatClient(noCitations);
        var service = Build(enabled: true, chat, new FakePlanSource(SamplePayload()));

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.Null(response);
        Assert.True(cached);
    }

    [Fact]
    public async Task Unknown_plan_returns_null_false_without_calling_model()
    {
        // Plan not found → (null, false) → endpoint 404. Model never called.
        var chat = new FakeChatClient(ValidModelJson);
        var planSource = new FakePlanSource(payload: null); // resolves to nothing
        var service = Build(enabled: true, chat, planSource);

        var (response, cached) = await service.GenerateAsync("nope", CancellationToken.None);

        Assert.Null(response);
        Assert.False(cached);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Happy_path_returns_populated_response_not_cached()
    {
        var chat = new FakeChatClient(ValidModelJson);
        var service = Build(enabled: true, chat, new FakePlanSource(SamplePayload()));

        var (response, cached) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(cached);
        Assert.Equal("Two loads share the Laredo->Dallas corridor.", response!.WhyReview);
        Assert.Equal("Confirm the combined driver RPM clears the floor.", response.WhatToVerify);
        Assert.Equal("Assign one driver and paste the click card.", response.NextAction);
        Assert.Contains("combinedRevenuePerMile", response.Citations);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task Code_fenced_json_is_parsed()
    {
        var chat = new FakeChatClient("```json\n" + ValidModelJson + "\n```");
        var service = Build(enabled: true, chat, new FakePlanSource(SamplePayload()));

        var (response, _) = await service.GenerateAsync("plan-1", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("blockers", response!.Citations);
    }

    // ---- helpers ----

    private static NarrativeService Build(
        bool enabled, INarrativeChatClient chat, INarrativePlanSource planSource)
    {
        var flags = new StaticOptionsMonitor<AiFeatureFlags>(new AiFeatureFlags { NarrativeEnabled = enabled });
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new NarrativeService(
            planSource, chat, cache, flags, NullLogger<NarrativeService>.Instance);
    }

    private static NarrativePlanPayload SamplePayload(string corridor = "LAREDO_TO_DALLAS") => new()
    {
        PlanId = "plan-1",
        CorridorCode = corridor,
        ParentLoadNumber = "L-100",
        ParentCustomerName = "Acme",
        ParentOrigin = "Laredo, TX",
        ParentDestination = "Dallas, TX",
        Siblings =
        [
            new NarrativePlanSibling
            {
                LoadId = "s1",
                LoadNumber = "L-101",
                CustomerName = "Beta",
                DestinationLabel = "Dallas, TX",
                Revenue = 1200m,
                WeightLbs = 8000m,
            },
        ],
        CombinedRevenue = 2400m,
        LinehaulMiles = 430m,
        DriverLoadedMiles = 430m,
        CombinedDriverTripValue = 900m,
        CombinedRevenuePerMile = 2.09m,
        RpmWarningStatus = "Ok",
        RpmWarningMessage = "Projected combined driver RPM is at or above the floor.",
        TrailerFitVerdict = null,
        Blockers = [],
        StopSequence = ["L-101"],
    };

    private sealed class FakeChatClient : INarrativeChatClient
    {
        private readonly Func<string, string?> _handler;

        public FakeChatClient(string? content) => _handler = _ => content;
        public FakeChatClient(Func<string, string?> handler) => _handler = handler;

        public int CallCount { get; private set; }

        public Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_handler(userPrompt));
        }
    }

    private sealed class FakePlanSource(NarrativePlanPayload? payload) : INarrativePlanSource
    {
        public NarrativePlanPayload? Payload { get; set; } = payload;
        public int CallCount { get; private set; }

        public Task<NarrativePlanPayload?> GetPlanPayloadAsync(string planId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Payload);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
