using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Deterministic behavior tests for the Phase 1 consolidation candidate service.
/// Every scenario is grounded in the yard-visit examples (Verdef → Goodyear, Masonite → Phoenix,
/// Kroger/Ring blocked, unknown customers get "confirm with account owner") so the tests double
/// as a behavior spec Jason can read alongside <c>docs/PILOT_LAREDO_DALLAS.md</c>.
/// </summary>
public sealed class ConsolidationCandidateServiceTests
{
    private static readonly DateTimeOffset SeedPickup =
        new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    private static ConsolidationOptions DefaultOptions() => new();

    private static ConsolidationCandidateService BuildService(
        FakeAlvysClient client,
        ConsolidationOptions? overrides = null)
    {
        var loads = new LtlLoadService(
            client,
            LtlTestFactory.Normalizer(),
            LtlTestFactory.Visibility(),
            LtlTestFactory.Options());

        return new ConsolidationCandidateService(
            loads,
            Options.Create(overrides ?? DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock());
    }

    private static AlvysLoad SeedLoad() => new()
    {
        Id = "SEED",
        LoadNumber = "L-100234",
        Status = "Available",
        CustomerName = "Verdef",
        Stops =
        [
            new AlvysLoadStop
            {
                StopType = "Pickup",
                Address = new AlvysAddress { City = "Laredo", State = "TX" },
                ScheduledStart = SeedPickup,
                Sequence = 1,
            },
            new AlvysLoadStop
            {
                StopType = "Delivery",
                Address = new AlvysAddress { City = "Dallas", State = "TX" },
                ScheduledStart = SeedPickup.AddDays(1),
                Sequence = 2,
            },
        ],
    };

    private static AlvysLoad Sibling(
        string id,
        string customer,
        string originCity,
        string originState,
        string destCity,
        string destState,
        DateTimeOffset pickup)
        => new()
        {
            Id = id,
            LoadNumber = id,
            Status = "Available",
            CustomerName = customer,
            Stops =
            [
                new AlvysLoadStop
                {
                    StopType = "Pickup",
                    Address = new AlvysAddress { City = originCity, State = originState },
                    ScheduledStart = pickup,
                    Sequence = 1,
                },
                new AlvysLoadStop
                {
                    StopType = "Delivery",
                    Address = new AlvysAddress { City = destCity, State = destState },
                    ScheduledStart = pickup.AddDays(1),
                    Sequence = 2,
                },
            ],
        };

    [Fact]
    public async Task Unknown_seed_returns_empty_response_not_error()
    {
        var client = new FakeAlvysClient(); // no LoadDetail set → GetLoadAsync returns null
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("MISSING", "LAREDO_TO_DALLAS", default);

        Assert.Null(response.Seed);
        Assert.Empty(response.Candidates);
    }

    [Fact]
    public async Task Unknown_corridor_throws_invalid_operation()
    {
        var client = new FakeAlvysClient { LoadDetail = SeedLoad() };
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCandidatesAsync("SEED", "NOT_A_CORRIDOR", default));
    }

    [Fact]
    public async Task Sibling_in_corridor_with_matching_customer_scores_all_good()
    {
        var seed = SeedLoad();
        var sibling = Sibling("L-100237", "Verdef", "Laredo", "TX", "Fort Worth", "TX", SeedPickup.AddHours(6));

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, sibling],
        };

        var options = DefaultOptions();
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });

        var service = BuildService(client, options);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("L-100237", candidate.LoadNumber);
        Assert.False(candidate.IsBlocked);
        Assert.All(candidate.Factors, f => Assert.Equal(ConsolidationFit.Good, f.Fit));
    }

    [Fact]
    public async Task Sibling_belonging_to_Kroger_is_blocked_by_customer_policy()
    {
        var seed = SeedLoad();
        var sibling = Sibling("L-100252", "Kroger", "Laredo", "TX", "Fort Worth", "TX", SeedPickup);

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, sibling],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        Assert.True(candidate.IsBlocked);
        Assert.Equal(CustomerConsolidationTier.Never, candidate.CustomerTier);
        var customerFactor = candidate.Factors.Single(f => f.Name == "Customer");
        Assert.Equal(ConsolidationFit.Blocked, customerFactor.Fit);
    }

    [Fact]
    public async Task Sibling_belonging_to_Masonite_is_notify_tier()
    {
        var seed = SeedLoad();
        var sibling = Sibling("L-100241", "Masonite", "Laredo", "TX", "Fort Worth", "TX", SeedPickup);

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, sibling],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        Assert.False(candidate.IsBlocked);
        Assert.Equal(CustomerConsolidationTier.NotifyRequired, candidate.CustomerTier);
        var customerFactor = candidate.Factors.Single(f => f.Name == "Customer");
        Assert.Equal(ConsolidationFit.Tight, customerFactor.Fit);
    }

    [Fact]
    public async Task Unknown_customer_defaults_to_confirm_with_account_owner_not_silent_allow()
    {
        var seed = SeedLoad();
        var sibling = Sibling("L-100999", "Never Heard Of Them", "Laredo", "TX", "Dallas", "TX", SeedPickup);

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, sibling],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal(CustomerConsolidationTier.Unknown, candidate.CustomerTier);
        var customerFactor = candidate.Factors.Single(f => f.Name == "Customer");
        Assert.Equal(ConsolidationFit.Tight, customerFactor.Fit);
        Assert.Contains("confirm with account owner", customerFactor.Rationale);
    }

    [Fact]
    public async Task Sibling_outside_corridor_is_not_returned_not_blocked()
    {
        var seed = SeedLoad();
        // Not near Laredo, not near Dallas — should never appear as a candidate.
        var outOfCorridor = Sibling(
            "L-100777",
            "Verdef",
            "Miami",
            "FL",
            "Atlanta",
            "GA",
            SeedPickup);

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, outOfCorridor],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        Assert.Empty(response.Candidates);
    }

    [Fact]
    public async Task Timing_beyond_double_window_is_blocked()
    {
        var seed = SeedLoad();
        // Corridor window defaults to 2 days; 5 days out is beyond 2× → blocked.
        var late = Sibling("L-100888", "Verdef", "Laredo", "TX", "Dallas", "TX", SeedPickup.AddDays(5));

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, late],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        Assert.True(candidate.IsBlocked);
        var timing = candidate.Factors.Single(f => f.Name == "Timing fit");
        Assert.Equal(ConsolidationFit.Blocked, timing.Fit);
    }

    [Fact]
    public async Task Timing_missing_on_candidate_reports_unknown_not_good()
    {
        var seed = SeedLoad();
        var sibling = new AlvysLoad
        {
            Id = "L-100333",
            LoadNumber = "L-100333",
            Status = "Available",
            CustomerName = "Verdef",
            Stops =
            [
                new AlvysLoadStop
                {
                    StopType = "Pickup",
                    Address = new AlvysAddress { City = "Laredo", State = "TX" },
                    Sequence = 1,
                    // No ScheduledStart on purpose.
                },
                new AlvysLoadStop
                {
                    StopType = "Delivery",
                    Address = new AlvysAddress { City = "Dallas", State = "TX" },
                    Sequence = 2,
                },
            ],
        };

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, sibling],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        var candidate = Assert.Single(response.Candidates);
        var timing = candidate.Factors.Single(f => f.Name == "Timing fit");
        Assert.Equal(ConsolidationFit.Unknown, timing.Fit);
    }

    [Fact]
    public async Task Seed_is_never_returned_as_its_own_sibling()
    {
        var seed = SeedLoad();

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed], // only the seed exists in the corpus
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        Assert.NotNull(response.Seed);
        Assert.Empty(response.Candidates);
    }

    [Fact]
    public async Task Blocked_candidates_sort_after_non_blocked()
    {
        var seed = SeedLoad();
        var kroger = Sibling("L-BLOCK", "Kroger", "Laredo", "TX", "Fort Worth", "TX", SeedPickup);
        var masonite = Sibling("L-OK",    "Masonite", "Laredo", "TX", "Fort Worth", "TX", SeedPickup.AddHours(3));

        var client = new FakeAlvysClient
        {
            LoadDetail = seed,
            Loads = [seed, kroger, masonite],
        };
        var service = BuildService(client);

        var response = await service.GetCandidatesAsync("SEED", "LAREDO_TO_DALLAS", default);

        Assert.Equal(2, response.Candidates.Count);
        Assert.Equal("L-OK", response.Candidates[0].LoadNumber);
        Assert.Equal("L-BLOCK", response.Candidates[1].LoadNumber);
    }
}
