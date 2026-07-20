using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Unit tests for the Alvys writeback/sandbox readiness snapshot: it must report the active mode,
/// per-operation eligibility and explicit blockers, expose no secrets, and only mark sandbox
/// execution configured when every requirement is met.
/// </summary>
public sealed class AlvysReadinessServiceTests
{
    private static AlvysReadinessService Service(
        AlvysWritebackMode mode = AlvysWritebackMode.Disabled,
        string environment = "",
        string sandboxBaseUrl = "",
        bool hasCredentials = false,
        IAlvysSyncTracker? tracker = null)
    {
        var write = Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions
        {
            Mode = mode,
            Environment = environment,
            SandboxBaseUrl = sandboxBaseUrl,
        });
        var alvys = Microsoft.Extensions.Options.Options.Create(new AlvysOptions
        {
            ClientId = hasCredentials ? "id" : "",
            ClientSecret = hasCredentials ? "secret" : "",
        });
        return new AlvysReadinessService(write, alvys, tracker ?? new InMemoryAlvysSyncTracker());
    }

    [Fact]
    public void Disabled_mode_reports_audit_only_operations()
    {
        var status = Service().GetStatus();

        Assert.Equal(AlvysWritebackMode.Disabled, status.WritebackMode);
        Assert.False(status.WritebackEnabled);
        Assert.False(status.SandboxExecutionConfigured);
        Assert.All(status.Operations, o =>
            Assert.Equal(AlvysOperationEligibility.AuditOnly, o.Eligibility));
    }

    [Fact]
    public void Simulation_mode_reports_simulation_only_operations()
    {
        var status = Service(AlvysWritebackMode.Simulation).GetStatus();

        Assert.True(status.WritebackEnabled);
        Assert.False(status.SandboxExecutionConfigured);
        Assert.All(status.Operations, o =>
            Assert.Equal(AlvysOperationEligibility.SimulationOnly, o.Eligibility));
    }

    [Fact]
    public void Sandbox_mode_fully_configured_reports_sandbox_eligible_operations()
    {
        // All operations are Supported; fully configured sandbox marks every operation as eligible.
        var status = Service(
            AlvysWritebackMode.Sandbox, environment: "sandbox",
            sandboxBaseUrl: "https://sandbox.example.com", hasCredentials: true).GetStatus();

        Assert.True(status.SandboxExecutionConfigured);
        Assert.Empty(status.Blockers);
        Assert.All(status.Operations, o =>
        {
            Assert.Equal(AlvysOperationEligibility.SandboxEligible, o.Eligibility);
            Assert.Null(o.RequiredToEnable);
        });
    }

    [Fact]
    public void Sandbox_mode_unconfigured_lists_blockers_and_is_not_configured()
    {
        var status = Service(AlvysWritebackMode.Sandbox).GetStatus();

        Assert.False(status.SandboxExecutionConfigured);
        Assert.NotEmpty(status.Blockers);
    }

    [Fact]
    public void Status_exposes_no_secret_values()
    {
        var status = Service(hasCredentials: true).GetStatus();

        // Only a boolean credential flag and the host root are surfaced — never id/secret/token.
        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        Assert.DoesNotContain("secret", serialized, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(status.HasCredentials);
    }

    [Fact]
    public void Last_read_sync_is_surfaced_from_the_tracker()
    {
        var tracker = new InMemoryAlvysSyncTracker();
        var at = DateTimeOffset.Parse("2026-06-30T12:00:00Z");
        tracker.Record(AlvysSyncOutcome.Success, at, "reached users/search");

        var status = Service(tracker: tracker).GetStatus();

        Assert.Equal(AlvysSyncOutcome.Success, status.LastReadSyncOutcome);
        Assert.Equal(at, status.LastReadSyncAt);
    }

    [Fact]
    public void Operations_cover_the_public_registry()
    {
        var status = Service().GetStatus();

        // Readiness surfaces Public-API operations only; internal-API operations (observed-not-
        // contracted, decision #10) are intentionally excluded from the readiness surface.
        var expected = AlvysWriteOperationRegistry.All
            .Count(op => op.Surface == AlvysWriteApiSurface.Public);

        Assert.Equal(expected, status.Operations.Count);
        Assert.Contains(status.Operations, o => o.Code == "create-load-note");
        Assert.Contains(status.Operations, o => o.Code == "load-update" && o.RequiresEtag);
        Assert.DoesNotContain(status.Operations, o => o.Code == "set-trip-references");
    }
}
