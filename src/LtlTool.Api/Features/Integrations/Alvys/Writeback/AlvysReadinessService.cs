using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>How far a single write operation can go under the current configuration.</summary>
public enum AlvysOperationEligibility
{
    /// <summary>Writeback disabled — payloads can be previewed/recorded for audit only.</summary>
    AuditOnly,
    /// <summary>Simulation mode — payloads can be dry-run previewed but never sent.</summary>
    SimulationOnly,
    /// <summary>Sandbox mode and fully configured — eligible to execute against the sandbox.</summary>
    SandboxEligible,
    /// <summary>No documented mutating endpoint — cannot execute live regardless of mode.</summary>
    Unsupported,
}

/// <summary>Readiness of a single write operation, for the operational-readiness UI panel.</summary>
public sealed class AlvysOperationReadiness
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string WorkflowStage { get; init; }
    public bool RequiresEtag { get; init; }
    public required AlvysOperationEligibility Eligibility { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public string? RequiredToEnable { get; init; }
}

/// <summary>
/// The Alvys sandbox/writeback readiness snapshot: provider + auth/config readiness, the active
/// writeback mode, whether sandbox execution is configured, the last read-sync result, the
/// top-level blockers, and per-operation eligibility. Carries no secrets — only the host root and
/// a boolean for credential presence.
/// </summary>
public sealed class AlvysReadinessStatus
{
    public required AlvysProvider Provider { get; init; }
    public required bool HasCredentials { get; init; }

    /// <summary>Host root for the read source-of-truth API (no secret, no token).</summary>
    public required string ApiBaseUrl { get; init; }

    public required AlvysWritebackMode WritebackMode { get; init; }
    public required string Environment { get; init; }

    /// <summary>True when any writeback (simulation or sandbox) is enabled.</summary>
    public required bool WritebackEnabled { get; init; }

    /// <summary>True only when sandbox mode is selected AND fully configured for execution.</summary>
    public required bool SandboxExecutionConfigured { get; init; }

    /// <summary>Sandbox host root, when configured (no secret).</summary>
    public string? SandboxBaseUrl { get; init; }

    public required AlvysSyncOutcome LastReadSyncOutcome { get; init; }
    public DateTimeOffset? LastReadSyncAt { get; init; }
    public string? LastReadSyncDetail { get; init; }

    /// <summary>Top-level reasons sandbox writeback is not currently active.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    public required IReadOnlyList<AlvysOperationReadiness> Operations { get; init; }
}

/// <summary>Computes the Alvys writeback/sandbox readiness snapshot from configuration.</summary>
public interface IAlvysReadinessService
{
    AlvysReadinessStatus GetStatus();
}

/// <inheritdoc cref="IAlvysReadinessService"/>
public sealed class AlvysReadinessService(
    IOptions<AlvysWriteOptions> writeOptions,
    IOptions<AlvysOptions> alvysOptions,
    IAlvysSyncTracker syncTracker) : IAlvysReadinessService
{
    private readonly AlvysWriteOptions _write = writeOptions.Value;
    private readonly AlvysOptions _alvys = alvysOptions.Value;

    public AlvysReadinessStatus GetStatus()
    {
        var sync = syncTracker.Current;
        var topBlockers = SandboxConfigBlockers();
        var sandboxConfigured = _write.Mode == AlvysWritebackMode.Sandbox && topBlockers.Count == 0;

        var operations = AlvysWriteOperationRegistry.All
            .Select(op => BuildOperationReadiness(op, topBlockers, sandboxConfigured))
            .ToList();

        return new AlvysReadinessStatus
        {
            Provider = _alvys.Provider,
            HasCredentials = _alvys.HasCredentials,
            ApiBaseUrl = _alvys.ApiBaseUrl,
            WritebackMode = _write.Mode,
            Environment = _write.Environment,
            WritebackEnabled = _write.Mode != AlvysWritebackMode.Disabled,
            SandboxExecutionConfigured = sandboxConfigured,
            SandboxBaseUrl = string.IsNullOrWhiteSpace(_write.SandboxBaseUrl) ? null : _write.SandboxBaseUrl,
            LastReadSyncOutcome = sync.Outcome,
            LastReadSyncAt = sync.At,
            LastReadSyncDetail = sync.Detail,
            Blockers = topBlockers,
            Operations = operations,
        };
    }

    private AlvysOperationReadiness BuildOperationReadiness(
        AlvysWriteOperationDescriptor op, IReadOnlyList<string> configBlockers, bool sandboxConfigured)
    {
        var blockers = new List<string>();
        AlvysOperationEligibility eligibility;

        if (op.LiveSupport == AlvysLiveSupport.Unsupported)
        {
            blockers.Add("No documented Alvys mutating endpoint for this operation.");
            eligibility = _write.Mode switch
            {
                AlvysWritebackMode.Disabled => AlvysOperationEligibility.AuditOnly,
                AlvysWritebackMode.Simulation => AlvysOperationEligibility.SimulationOnly,
                _ => AlvysOperationEligibility.Unsupported,
            };
        }
        else if (sandboxConfigured)
        {
            eligibility = AlvysOperationEligibility.SandboxEligible;
        }
        else
        {
            blockers.AddRange(configBlockers);
            eligibility = _write.Mode == AlvysWritebackMode.Disabled
                ? AlvysOperationEligibility.AuditOnly
                : AlvysOperationEligibility.SimulationOnly;
        }

        return new AlvysOperationReadiness
        {
            Code = op.Code,
            Title = op.Title,
            WorkflowStage = op.WorkflowStage,
            RequiresEtag = op.RequiresEtag,
            Eligibility = eligibility,
            Blockers = blockers,
            RequiredToEnable = op.LiveSupport == AlvysLiveSupport.Unsupported ? op.RequiredToEnable : null,
        };
    }

    /// <summary>
    /// Reasons sandbox execution is not configured, independent of any single operation. Empty only
    /// when mode is Sandbox, the environment is a recognised sandbox, a sandbox base URL is set and
    /// credentials are present.
    /// </summary>
    private List<string> SandboxConfigBlockers()
    {
        var blockers = new List<string>();

        if (_write.Mode != AlvysWritebackMode.Sandbox)
        {
            blockers.Add($"Writeback mode is {_write.Mode}, not Sandbox.");
            return blockers;
        }

        if (!_write.IsRecognisedSandboxEnvironment)
            blockers.Add($"Environment '{_write.Environment}' is not a recognised non-production sandbox.");
        if (!_write.HasSandboxBaseUrl)
            blockers.Add("No sandbox base URL is configured (or it points at the production host).");
        if (!_alvys.HasCredentials)
            blockers.Add("Alvys credentials are not configured.");

        return blockers;
    }
}
