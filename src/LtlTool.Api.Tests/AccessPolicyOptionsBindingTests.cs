using LtlTool.Api.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Proves the effective allow-list survives the exact .NET config-binding path the running app
/// uses (env-var double-underscore keys → <c>AccessPolicy</c> section → <see cref="AccessPolicyOptions"/>),
/// for both config shapes that ops tooling writes. Root-caused the UAT 403: a comma-separated
/// value written to a single indexed element (<c>AccessPolicy__AllowedEmailDomains__0</c>) matched
/// no one under the old exact-per-element comparison.
/// </summary>
public sealed class AccessPolicyOptionsBindingTests
{
    private static AccessPolicyOptions Bind(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var options = new AccessPolicyOptions();
        config.GetSection("AccessPolicy").Bind(options);
        return options;
    }

    [Fact]
    public void Indexed_single_domain_binds_and_admits_target_user()
    {
        var options = Bind(("AccessPolicy:AllowedEmailDomains:0", "valuetruck.com"));
        Assert.Equal(["valuetruck.com"], options.NormalizedEmailDomains);
    }

    [Fact]
    public void Indexed_two_domains_bind_to_both()
    {
        var options = Bind(
            ("AccessPolicy:AllowedEmailDomains:0", "valuetruck.com"),
            ("AccessPolicy:AllowedEmailDomains:1", "valuelogistics.com"));
        Assert.Equal(["valuetruck.com", "valuelogistics.com"], options.NormalizedEmailDomains);
    }

    [Fact]
    public void Comma_separated_value_in_single_element_flattens_to_both_domains()
    {
        // This is exactly what infra/main.bicep writes when fed the multi-domain repo variable
        // ALLOWED_EMAIL_DOMAINS=valuetruck.com,valuelogistics.com — previously the 403 cause.
        var options = Bind(
            ("AccessPolicy:AllowedEmailDomains:0", "valuetruck.com,valuelogistics.com"));
        Assert.Equal(["valuetruck.com", "valuelogistics.com"], options.NormalizedEmailDomains);
    }

    [Fact]
    public void Empty_config_yields_empty_list_admit_all_semantics()
    {
        var options = Bind();
        Assert.Empty(options.NormalizedEmailDomains);
    }

    [Fact]
    public void Blank_single_element_yields_empty_list_admit_all_semantics()
    {
        // infra/main.bicep sets __0='' when no domain is configured; must NOT deny-all.
        var options = Bind(("AccessPolicy:AllowedEmailDomains:0", ""));
        Assert.Empty(options.NormalizedEmailDomains);
    }
}
