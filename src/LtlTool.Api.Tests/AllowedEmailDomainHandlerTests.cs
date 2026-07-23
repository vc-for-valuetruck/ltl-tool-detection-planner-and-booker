using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LtlTool.Api.Options;
using LtlTool.Api.Security;
using Xunit;

namespace LtlTool.Api.Tests;

public sealed class AllowedEmailDomainHandlerTests
{
    private static async Task<bool> EvaluateClaimsAsync(
        string[] allowedDomains, params (string Type, string Value)[] claims)
    {
        var handler = new AllowedEmailDomainHandler(
            Microsoft.Extensions.Options.Options.Create(
                new AccessPolicyOptions { AllowedEmailDomains = allowedDomains }),
            NullLogger<AllowedEmailDomainHandler>.Instance);

        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)), "test");
        var user = new ClaimsPrincipal(identity);
        var requirement = new AllowedEmailDomainRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // v2.0-shaped principal: identity in preferred_username (the previous behavior).
    private static Task<bool> EvaluateAsync(string[] allowedDomains, string? email)
        => email is null
            ? EvaluateClaimsAsync(allowedDomains)
            : EvaluateClaimsAsync(allowedDomains, ("preferred_username", email));

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

    // --- UAT 403 regression coverage (v1.0 token shape: no preferred_username) ---
    // The API issues v1.0 access tokens; identity arrives in upn / unique_name / email, and
    // ASP.NET may surface some of those under the mapped ClaimTypes.* URIs. The handler must
    // admit a valuetruck.com user regardless of which of these carries the identity.

    [Fact]
    public async Task V1_token_upn_short_claim_admits_matching_user()
        => Assert.True(await EvaluateClaimsAsync(
            ["valuetruck.com"], ("upn", "joshua.davis@valuetruck.com")));

    [Fact]
    public async Task V1_token_mapped_upn_claim_admits_matching_user()
        => Assert.True(await EvaluateClaimsAsync(
            ["valuetruck.com"], (ClaimTypes.Upn, "joshua.davis@valuetruck.com")));

    [Fact]
    public async Task V1_token_unique_name_claim_admits_matching_user()
        => Assert.True(await EvaluateClaimsAsync(
            ["valuetruck.com"], ("unique_name", "joshua.davis@valuetruck.com")));

    [Fact]
    public async Task V1_token_short_email_claim_admits_matching_user()
        => Assert.True(await EvaluateClaimsAsync(
            ["valuetruck.com"], ("email", "joshua.davis@valuetruck.com")));

    [Fact]
    public async Task V1_token_mapped_email_claim_admits_matching_user()
        => Assert.True(await EvaluateClaimsAsync(
            ["valuetruck.com"], (ClaimTypes.Email, "joshua.davis@valuetruck.com")));

    [Fact]
    public async Task V1_token_display_name_without_at_is_not_a_domain_and_is_denied()
        => Assert.False(await EvaluateClaimsAsync(
            ["valuetruck.com"], (ClaimTypes.Name, "Joshua Davis")));

    [Fact]
    public async Task V1_token_upn_on_unlisted_domain_is_denied()
        => Assert.False(await EvaluateClaimsAsync(
            ["valuetruck.com"], ("upn", "attacker@evil.com")));
}
