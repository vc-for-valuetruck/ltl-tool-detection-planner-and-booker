using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Tests.Ltl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Two suites: fast unit-level exercises of <see cref="CustomerNotesLtlPolicyReader.ParseTierFromNotes"/>
/// (no client hop), plus end-to-end tests through the <see cref="ICustomerLtlPolicyReader"/> surface
/// using an in-memory customer list via <see cref="LtlTestFactory.NewAlvysClient"/> style fake.
/// </summary>
public class CustomerNotesLtlPolicyReaderTests
{
    // ---- ParseTierFromNotes: focused parser suite ----

    [Fact]
    public void ParseTierFromNotes_returns_Allowed_when_LTL_TIER_is_Allowed()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "General note.\nLTL_TIER=Allowed" },
        });
        Assert.Equal(CustomerConsolidationTier.Allowed, tier);
    }

    [Fact]
    public void ParseTierFromNotes_is_case_insensitive()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "ltl_tier = notifyrequired  # confirm w/ Junior" },
        });
        Assert.Equal(CustomerConsolidationTier.NotifyRequired, tier);
    }

    [Fact]
    public void ParseTierFromNotes_returns_Never_when_LTL_TIER_is_Never()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "LTL_TIER=Never — brand-sensitive" },
        });
        Assert.Equal(CustomerConsolidationTier.Never, tier);
    }

    [Fact]
    public void ParseTierFromNotes_falls_back_to_LTL_ALLOW_true_when_no_TIER_present()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "LTL_ALLOW=true" },
        });
        Assert.Equal(CustomerConsolidationTier.Allowed, tier);
    }

    [Fact]
    public void ParseTierFromNotes_falls_back_to_LTL_ALLOW_false_as_Never()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "LTL_ALLOW=false" },
        });
        Assert.Equal(CustomerConsolidationTier.Never, tier);
    }

    [Fact]
    public void ParseTierFromNotes_returns_null_when_no_LTL_marker_present()
    {
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "Called warehouse, POD confirmed." },
            new AlvysContextNote { Description = "Payment terms updated." },
        });
        Assert.Null(tier); // caller falls back to static config
    }

    [Fact]
    public void ParseTierFromNotes_LTL_TIER_wins_over_LTL_ALLOW_when_both_present()
    {
        // Someone edited notes to tighten the policy: LTL_TIER=NotifyRequired should win over
        // a legacy LTL_ALLOW=true. Precedence prevents silent regressions.
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "LTL_ALLOW=true\nLTL_TIER=NotifyRequired" },
        });
        Assert.Equal(CustomerConsolidationTier.NotifyRequired, tier);
    }

    [Fact]
    public void ParseTierFromNotes_returns_first_match_across_notes()
    {
        // Notes are ordered oldest-first in Alvys; taking the first match is a policy call
        // \u2014 we want the most recent edit to win. Alvys returns notes newest-first in most
        // endpoints, so first-match matches operator intent.
        var tier = CustomerNotesLtlPolicyReader.ParseTierFromNotes(new[]
        {
            new AlvysContextNote { Description = "LTL_TIER=Allowed" },
            new AlvysContextNote { Description = "LTL_TIER=Never" },
        });
        Assert.Equal(CustomerConsolidationTier.Allowed, tier);
    }

    // ---- Integration with static-config fallback ----

    [Fact]
    public async Task ResolveAsync_prefers_notes_over_static_config()
    {
        var client = new FakeAlvysClient();
        client.Customers.Add(new AlvysCustomer
        {
            Id = "CUST-1",
            Name = "Kroger",
            Notes = new() { new AlvysContextNote { Description = "LTL_TIER=Allowed" } },
        });

        // Static config marks Kroger as Never, but the notes say Allowed. Notes win.
        var options = new ConsolidationOptions
        {
            CustomerPolicies = new()
            {
                new() { Customer = "Kroger", Tier = CustomerConsolidationTier.Never },
            },
        };

        var reader = new CustomerNotesLtlPolicyReader(
            client,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CustomerNotesLtlPolicyReader>.Instance);

        var resolution = await reader.ResolveAsync("CUST-1", "Kroger", default);
        Assert.Equal(CustomerConsolidationTier.Allowed, resolution.Tier);
        Assert.Equal(CustomerPolicySource.CustomerNote, resolution.Source);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_static_config_when_no_LTL_notes()
    {
        var client = new FakeAlvysClient();
        client.Customers.Add(new AlvysCustomer
        {
            Id = "CUST-2",
            Name = "Masonite",
            Notes = new() { new AlvysContextNote { Description = "Prefers morning deliveries." } },
        });

        var options = new ConsolidationOptions
        {
            CustomerPolicies = new()
            {
                new() { Customer = "Masonite", Tier = CustomerConsolidationTier.NotifyRequired },
            },
        };

        var reader = new CustomerNotesLtlPolicyReader(
            client, Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CustomerNotesLtlPolicyReader>.Instance);

        var resolution = await reader.ResolveAsync("CUST-2", "Masonite", default);
        Assert.Equal(CustomerConsolidationTier.NotifyRequired, resolution.Tier);
        Assert.Equal(CustomerPolicySource.DefaultPolicy, resolution.Source);
    }

    [Fact]
    public async Task ResolveAsync_returns_Unknown_when_customer_absent_and_no_static_policy()
    {
        var client = new FakeAlvysClient();
        // No customers seeded. No static policy.

        var reader = new CustomerNotesLtlPolicyReader(
            client, Microsoft.Extensions.Options.Options.Create(new ConsolidationOptions()),
            NullLogger<CustomerNotesLtlPolicyReader>.Instance);

        var resolution = await reader.ResolveAsync("nope", "Unknown Customer", default);
        Assert.Equal(CustomerConsolidationTier.Unknown, resolution.Tier);
        Assert.Equal(CustomerPolicySource.None, resolution.Source);
    }

    [Fact]
    public async Task ResolveAsync_returns_Unknown_when_both_inputs_are_null()
    {
        var client = new FakeAlvysClient();
        var reader = new CustomerNotesLtlPolicyReader(
            client, Microsoft.Extensions.Options.Options.Create(new ConsolidationOptions()),
            NullLogger<CustomerNotesLtlPolicyReader>.Instance);

        var resolution = await reader.ResolveAsync(null, null, default);
        Assert.Equal(CustomerConsolidationTier.Unknown, resolution.Tier);
        Assert.Equal(CustomerPolicySource.None, resolution.Source);
    }

    [Fact]
    public async Task ResolveAsync_caches_repeated_lookups()
    {
        // Cache is important on real workloads \u2014 a plan with 15 candidates would otherwise
        // do 15 SearchCustomersAsync calls. This test uses a counting fake to prove one hit.
        var client = new FakeAlvysClient();
        client.Customers.Add(new AlvysCustomer
        {
            Id = "CUST-3",
            Name = "AcmeShipCo",
            Notes = new() { new AlvysContextNote { Description = "LTL_TIER=Allowed" } },
        });

        var reader = new CustomerNotesLtlPolicyReader(
            client, Microsoft.Extensions.Options.Options.Create(new ConsolidationOptions()),
            NullLogger<CustomerNotesLtlPolicyReader>.Instance);

        _ = await reader.ResolveAsync("CUST-3", "AcmeShipCo", default);
        _ = await reader.ResolveAsync("CUST-3", "AcmeShipCo", default);
        _ = await reader.ResolveAsync("CUST-3", "AcmeShipCo", default);

        Assert.Equal(1, client.SearchCustomersCallCount);
    }
}
