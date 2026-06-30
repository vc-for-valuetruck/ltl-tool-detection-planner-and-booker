using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Unit tests for the Alvys writeback boundary. The overriding invariant: no path ever executes a
/// live Alvys mutation in this phase — every disposition is audit-only, simulated, blocked or
/// unsupported, and <c>Executed</c> is always false.
/// </summary>
public sealed class AlvysWriteGatewayTests
{
    private static AlvysWriteGateway Gateway(
        AlvysWritebackMode mode = AlvysWritebackMode.Disabled,
        string environment = "",
        string sandboxBaseUrl = "",
        bool hasCredentials = false)
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
        return new AlvysWriteGateway(write, alvys);
    }

    private static AlvysOperationRequest ValidNote() =>
        new() { LoadNumber = "L100", NoteText = "Assigned to driver D1 over a tight window." };

    [Fact]
    public void DryRun_builds_note_payload_and_never_executes()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation).DryRun("create-load-note", ValidNote());

        Assert.False(outcome.Executed);
        Assert.Equal(AlvysOperationDisposition.Simulated, outcome.Disposition);
        Assert.NotNull(outcome.Payload);
        Assert.Equal("Assigned to driver D1 over a tight window.", outcome.Payload!.Body["Description"]);
        // Default NoteType is General (Alvys accepted values: System/General/Assignment/Safety).
        Assert.Equal("General", outcome.Payload.Body["NoteType"]);
        // The note Id is generated at dispatch time (write client), not in the deterministic
        // payload, so equivalent note requests still de-duplicate by idempotency key.
        Assert.False(outcome.Payload.Body.ContainsKey("Id"));
    }

    [Fact]
    public void Disabled_mode_is_audit_only()
    {
        var outcome = Gateway(AlvysWritebackMode.Disabled).Execute("create-load-note", ValidNote());

        Assert.Equal(AlvysOperationDisposition.AuditOnly, outcome.Disposition);
        Assert.False(outcome.Executed);
        Assert.NotNull(outcome.Payload);
    }

    [Fact]
    public void Simulation_mode_is_simulated_not_sent()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation).Execute("create-load-note", ValidNote());

        Assert.Equal(AlvysOperationDisposition.Simulated, outcome.Disposition);
        Assert.False(outcome.Executed);
    }

    [Fact]
    public void Sandbox_mode_fully_configured_signals_sandbox_execution_eligible()
    {
        // Fully configured sandbox + Supported operation: gateway signals eligibility; the recorder
        // dispatches the live call so the gateway itself never executes.
        var outcome = Gateway(
                AlvysWritebackMode.Sandbox, environment: "sandbox",
                sandboxBaseUrl: "https://sandbox.example.com", hasCredentials: true)
            .Execute("create-load-note", ValidNote());

        Assert.Equal(AlvysOperationDisposition.SandboxExecuted, outcome.Disposition);
        Assert.True(outcome.SandboxExecutionEligible);
        Assert.False(outcome.Executed);
        Assert.Null(outcome.RequiredToEnable);
    }

    [Fact]
    public void Missing_required_fields_block_with_validation_and_no_payload()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .Execute("create-load-note", new AlvysOperationRequest { LoadNumber = "L100" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "NOTE_TEXT_REQUIRED");
        Assert.Null(outcome.Payload);
    }

    [Fact]
    public void Etag_required_operations_block_without_an_etag()
    {
        // tender-accept blocks on missing ETag and missing StopCompanyLinks.
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .Execute("tender-accept", new AlvysOperationRequest { TenderId = "T1" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "ETAG_REQUIRED");
        Assert.Contains(outcome.Validation, i => i.Code == "STOP_COMPANY_LINKS_REQUIRED");
    }

    [Fact]
    public void Etag_required_operation_builds_payload_when_etag_and_links_supplied()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .DryRun("tender-accept", new AlvysOperationRequest
            {
                TenderId = "T1",
                Etag = "v1",
                StopCompanyLinks = [new TenderStopCompanyLink { StopId = "S1", CompanyId = "C1" }],
            });

        Assert.Equal(AlvysOperationDisposition.Simulated, outcome.Disposition);
        Assert.NotNull(outcome.Payload);
        Assert.True(outcome.Payload!.RequiresEtag);
        Assert.True(outcome.Payload.EtagSupplied);
        Assert.True(outcome.Payload.Body.ContainsKey("StopCompanyLinks"));
    }

    [Fact]
    public void Tender_accept_rejects_blank_stop_company_links()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .Execute("tender-accept", new AlvysOperationRequest
            {
                TenderId = "T1",
                Etag = "v1",
                StopCompanyLinks = [new TenderStopCompanyLink { StopId = "", CompanyId = "" }],
            });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "STOP_COMPANY_LINK_INVALID");
    }

    [Fact]
    public void Load_update_requires_fields_and_etag()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .Execute("load-update", new AlvysOperationRequest { LoadNumber = "L100" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "FIELDS_REQUIRED");
        Assert.Contains(outcome.Validation, i => i.Code == "ETAG_REQUIRED");
    }

    [Fact]
    public void Load_update_payload_carries_writable_order_number()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation).DryRun("load-update",
            new AlvysOperationRequest
            {
                LoadNumber = "L100",
                Etag = "v2",
                Fields = new() { ["OrderNumber"] = "SHIP-100245" },
            });

        Assert.NotNull(outcome.Payload);
        Assert.Equal("SHIP-100245", outcome.Payload!.Body["OrderNumber"]);
    }

    [Fact]
    public void Load_update_rejects_fields_outside_the_writable_allowlist()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation).Execute("load-update",
            new AlvysOperationRequest
            {
                LoadNumber = "L100",
                Etag = "v2",
                Fields = new() { ["Status"] = "Delivered", ["CustomerId"] = "C9" },
            });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "FIELD_NOT_WRITABLE");
        Assert.Null(outcome.Payload);
    }

    [Fact]
    public void Load_update_rejects_order_number_longer_than_30_chars()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation).Execute("load-update",
            new AlvysOperationRequest
            {
                LoadNumber = "L100",
                Etag = "v2",
                Fields = new() { ["OrderNumber"] = new string('X', 31) },
            });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "ORDER_NUMBER_TOO_LONG");
    }

    [Fact]
    public void Trip_stop_arrival_requires_trip_stop_and_timestamp()
    {
        var outcome = Gateway(AlvysWritebackMode.Simulation)
            .Execute("trip-stop-arrival", new AlvysOperationRequest { TripId = "TR1" });

        Assert.Equal(AlvysOperationDisposition.Blocked, outcome.Disposition);
        Assert.Contains(outcome.Validation, i => i.Code == "STOP_ID_REQUIRED");
        Assert.Contains(outcome.Validation, i => i.Code == "ARRIVED_AT_REQUIRED");
    }

    [Fact]
    public void Unknown_operation_is_unsupported()
    {
        var outcome = Gateway().Execute("delete-everything", new AlvysOperationRequest());

        Assert.Equal(AlvysOperationDisposition.Unsupported, outcome.Disposition);
        Assert.False(outcome.Executed);
    }

    [Fact]
    public void Sandbox_blockers_list_each_missing_config_item()
    {
        // Sandbox mode but nothing configured: expect environment + base-url + credential blockers.
        var outcome = Gateway(AlvysWritebackMode.Sandbox)
            .DryRun("create-load-note", ValidNote());

        Assert.Contains(outcome.Blockers, b => b.Contains("not a recognised non-production sandbox"));
        Assert.Contains(outcome.Blockers, b => b.Contains("sandbox base URL"));
        Assert.Contains(outcome.Blockers, b => b.Contains("credentials"));
    }

    [Fact]
    public void Production_host_as_sandbox_base_url_is_rejected()
    {
        var outcome = Gateway(
                AlvysWritebackMode.Sandbox, environment: "sandbox",
                sandboxBaseUrl: "https://integrations.alvys.com", hasCredentials: true)
            .DryRun("create-load-note", ValidNote());

        Assert.Contains(outcome.Blockers, b => b.Contains("sandbox base URL"));
    }
}
