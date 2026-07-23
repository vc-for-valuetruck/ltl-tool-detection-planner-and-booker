using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.DispatchAssist;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.DispatchAssist;

/// <summary>
/// Alvys-backed integration behavior for the Dispatch Assist engine — the parts that sweep the fake
/// Alvys client rather than the pure <c>Rank</c> scorer (covered in
/// <see cref="LtlTool.Api.Tests.Ltl.DispatchAssistServiceTests"/>). Covers the ad-hoc-lane assembly +
/// ranking, the honest 404 path, and the read-only notify-recipient resolution (driver + dispatcher),
/// which must surface an intended-but-unaddressable recipient rather than fabricating an address.
/// </summary>
public sealed class DispatchAssistServiceTests
{
    private static DispatchAssistService Build(FakeAlvysClient client, LtlOptions? options = null)
    {
        var opts = LtlTestFactory.Options(options);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(options), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            opts, LtlTestFactory.Clock());
        return new DispatchAssistService(
            client, loads, opts, NullLogger<DispatchAssistService>.Instance);
    }

    [Fact]
    public async Task Recommend_returns_null_when_supplied_loadId_is_unresolvable()
    {
        var svc = Build(new FakeAlvysClient { LoadDetail = null });

        var result = await svc.RecommendAsync("MISSING", null, null, null, null, 5, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task Recommend_ranks_a_preferred_pairing_ahead_on_an_ad_hoc_lane()
    {
        var client = new FakeAlvysClient
        {
            Drivers =
            [
                new AlvysDriver { Id = "D1", Name = "Preferred", IsActive = true, Address = new AlvysContextAddress { State = "TX" } },
                new AlvysDriver { Id = "D2", Name = "Other", IsActive = true, Address = new AlvysContextAddress { State = "TX" } },
            ],
            Trucks = [new AlvysTruck { Id = "T1", TruckNum = "214" }],
            DispatchPreferences =
            [
                new AlvysDispatchPreference { DispatcherId = "U1", Driver1Id = "D1", TruckId = "T1" },
            ],
        };
        var svc = Build(client);

        var result = await svc.RecommendAsync(null, "Dallas", "TX", null, null, 5, default);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Candidates);
        Assert.Equal("D1", result.Candidates[0].DriverId);
        Assert.True(result.Candidates[0].IsPreferredPairing);
        Assert.Contains("Caller-supplied", result.Target.Source);
    }

    [Fact]
    public async Task ResolveNotifyRecipients_maps_driver_and_dispatcher_from_alvys()
    {
        var client = new FakeAlvysClient
        {
            Drivers = [new AlvysDriver { Id = "D1", Name = "Pat Driver", Email = "pat@carrier.example" }],
            DispatchPreferences =
            [
                new AlvysDispatchPreference { DispatcherId = "U1", Driver1Id = "D1", TruckId = "T1" },
            ],
            Users = [new AlvysUser { Id = "U1", Name = "Dana Dispatcher", Email = "dana@valuetruck.com" }],
        };
        var svc = Build(client);

        var recipients = await svc.ResolveNotifyRecipientsAsync(
            new DispatchAssembleRequest { DriverId = "D1", TruckId = "T1" }, default);

        Assert.Contains(recipients, r => r.Role == "driver" && r.Address == "pat@carrier.example");
        Assert.Contains(recipients, r => r.Role == "dispatcher" && r.Address == "dana@valuetruck.com");
    }

    [Fact]
    public async Task ResolveNotifyRecipients_returns_driver_with_null_address_when_email_missing()
    {
        var client = new FakeAlvysClient
        {
            Drivers = [new AlvysDriver { Id = "D1", Name = "No Email" }],
        };
        var svc = Build(client);

        var recipients = await svc.ResolveNotifyRecipientsAsync(
            new DispatchAssembleRequest { DriverId = "D1" }, default);

        var driver = Assert.Single(recipients);
        Assert.Equal("driver", driver.Role);
        Assert.Null(driver.Address); // surfaced as intended-but-unaddressable, never fabricated
    }
}
