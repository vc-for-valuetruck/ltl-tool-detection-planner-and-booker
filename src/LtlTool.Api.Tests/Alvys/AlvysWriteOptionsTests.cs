using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Phase 0 stability guardrail: locks the two invariants that keep sandbox writeback from ever
/// reaching a live/production Alvys tenant. If either regresses, a writeback could physically
/// hit the production host — treat these as the highest-severity guardrail tests in the suite.
/// </summary>
public sealed class AlvysWriteOptionsTests
{
    [Fact]
    public void Defaults_are_disabled_and_unarmed()
    {
        var options = new AlvysWriteOptions();

        Assert.Equal(AlvysWritebackMode.Disabled, options.Mode);
        Assert.Equal("", options.Environment);
        Assert.Equal("", options.SandboxBaseUrl);
        Assert.False(options.IsRecognisedSandboxEnvironment);
        Assert.False(options.HasSandboxBaseUrl);
    }

    [Theory]
    [InlineData("sandbox")]
    [InlineData("uat")]
    [InlineData("staging")]
    [InlineData("test")]
    [InlineData("SANDBOX")]
    [InlineData("  UAT  ")]
    public void Recognised_sandbox_environments_are_case_insensitive_and_trimmed(string env)
    {
        var options = new AlvysWriteOptions { Environment = env };
        Assert.True(options.IsRecognisedSandboxEnvironment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("production")]
    [InlineData("prod")]
    [InlineData("live")]
    [InlineData("PRODUCTION")]
    public void Non_sandbox_environments_are_rejected(string env)
    {
        var options = new AlvysWriteOptions { Environment = env };
        Assert.False(options.IsRecognisedSandboxEnvironment);
    }

    [Fact]
    public void Empty_sandbox_base_url_is_not_armed()
    {
        var options = new AlvysWriteOptions { SandboxBaseUrl = "" };
        Assert.False(options.HasSandboxBaseUrl);
    }

    [Fact]
    public void Whitespace_sandbox_base_url_is_not_armed()
    {
        var options = new AlvysWriteOptions { SandboxBaseUrl = "   " };
        Assert.False(options.HasSandboxBaseUrl);
    }

    /// <summary>
    /// The single most important safety property in the writeback boundary: the real Alvys
    /// production read host is architecturally rejected as a sandbox base URL. Any change that
    /// weakens this predicate is a critical regression.
    /// </summary>
    [Theory]
    [InlineData("https://integrations.alvys.com")]
    [InlineData("https://integrations.alvys.com/")]
    [InlineData("https://integrations.alvys.com/api")]
    [InlineData("https://INTEGRATIONS.ALVYS.COM/api/p/v1")]
    [InlineData("http://integrations.alvys.com")]
    public void Production_host_is_never_treated_as_a_sandbox(string url)
    {
        var options = new AlvysWriteOptions { SandboxBaseUrl = url };
        Assert.False(options.HasSandboxBaseUrl);
    }

    [Theory]
    [InlineData("https://sandbox.alvys.com")]
    [InlineData("https://uat.alvys.example")]
    [InlineData("https://staging-integrations.internal")]
    [InlineData("https://alvys-test.local")]
    public void Non_production_hosts_are_accepted_as_sandbox(string url)
    {
        var options = new AlvysWriteOptions { SandboxBaseUrl = url };
        Assert.True(options.HasSandboxBaseUrl);
    }

    /// <summary>
    /// Both gates must clear before Sandbox mode can even be considered executable. Simulating
    /// what the readiness path checks: recognised environment AND non-production base URL.
    /// </summary>
    [Fact]
    public void Sandbox_requires_both_recognised_environment_and_non_production_url()
    {
        // Recognised env but production URL → still refused.
        var envOnly = new AlvysWriteOptions
        {
            Environment = "sandbox",
            SandboxBaseUrl = "https://integrations.alvys.com",
        };
        Assert.True(envOnly.IsRecognisedSandboxEnvironment);
        Assert.False(envOnly.HasSandboxBaseUrl);

        // Non-production URL but production-labelled env → still refused.
        var urlOnly = new AlvysWriteOptions
        {
            Environment = "production",
            SandboxBaseUrl = "https://sandbox.alvys.example",
        };
        Assert.False(urlOnly.IsRecognisedSandboxEnvironment);
        Assert.True(urlOnly.HasSandboxBaseUrl);

        // Both gates cleared → recognised as a valid sandbox posture.
        var armed = new AlvysWriteOptions
        {
            Environment = "sandbox",
            SandboxBaseUrl = "https://sandbox.alvys.example",
        };
        Assert.True(armed.IsRecognisedSandboxEnvironment);
        Assert.True(armed.HasSandboxBaseUrl);
    }
}
