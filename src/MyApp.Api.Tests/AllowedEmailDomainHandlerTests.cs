using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MyApp.Api.Options;
using MyApp.Api.Security;
using Xunit;

namespace MyApp.Api.Tests;

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
}
