using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using LtlTool.Api.Options;
using LtlTool.Api.Security;
using Xunit;

namespace LtlTool.Api.Tests;

public sealed class AllowedEmailDomainHandlerTests
{
    private static async Task<bool> EvaluateAsync(string[] allowedDomains, string? email)
    {
        var handler = new AllowedEmailDomainHandler(
            Microsoft.Extensions.Options.Options.Create(
                new AccessPolicyOptions { AllowedEmailDomains = allowedDomains }));

        var claims = email is null ? [] : new[] { new Claim("preferred_username", email) };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var requirement = new AllowedEmailDomainRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Empty_allow_list_permits_any_authenticated_user()
        => Assert.True(await EvaluateAsync([], "anyone@whatever.com"));

    [Fact]
    public async Task Matching_domain_is_allowed()
        => Assert.True(await EvaluateAsync(["example.com"], "user@example.com"));

    [Fact]
    public async Task Matching_domain_is_case_insensitive()
        => Assert.True(await EvaluateAsync(["example.com"], "User@Example.COM"));

    [Fact]
    public async Task Non_matching_domain_is_denied()
        => Assert.False(await EvaluateAsync(["example.com"], "user@evil.com"));

    [Fact]
    public async Task Missing_email_is_denied_when_allow_list_set()
        => Assert.False(await EvaluateAsync(["example.com"], null));

    // --- UAT 403 regression coverage (comma-separated value landing in a single array element) ---

    [Fact]
    public async Task Indexed_single_domain_admits_matching_user()
        => Assert.True(await EvaluateAsync(["valuetruck.com"], "joshua.davis@valuetruck.com"));

    [Fact]
    public async Task Comma_separated_single_element_admits_first_domain()
        => Assert.True(await EvaluateAsync(
            ["valuetruck.com,valuelogistics.com"], "joshua.davis@valuetruck.com"));

    [Fact]
    public async Task Comma_separated_single_element_admits_second_domain()
        => Assert.True(await EvaluateAsync(
            ["valuetruck.com,valuelogistics.com"], "ops@valuelogistics.com"));

    [Fact]
    public async Task Comma_separated_single_element_denies_unlisted_domain()
        => Assert.False(await EvaluateAsync(
            ["valuetruck.com,valuelogistics.com"], "user@evil.com"));

    [Fact]
    public async Task Indexed_multi_entry_admits_matching_user()
        => Assert.True(await EvaluateAsync(
            ["valuetruck.com", "valuelogistics.com"], "ops@valuelogistics.com"));

    [Fact]
    public async Task Whitespace_and_casing_in_config_are_normalized()
        => Assert.True(await EvaluateAsync(
            [" ValueTruck.COM , valuelogistics.com "], "joshua.davis@valuetruck.com"));

    [Fact]
    public async Task Blank_only_config_element_admits_any_authenticated_user()
        => Assert.True(await EvaluateAsync([""], "anyone@whatever.com"));

    [Fact]
    public async Task Whitespace_only_config_element_admits_any_authenticated_user()
        => Assert.True(await EvaluateAsync(["   "], "anyone@whatever.com"));
}
