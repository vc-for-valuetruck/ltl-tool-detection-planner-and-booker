namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// Seam for pushing an assembled driver+truck+trailer decision back to Alvys (trip-assign /
/// trip-dispatch). <b>This slice does not implement it.</b> The Alvys write boundary
/// (<c>Features/Integrations/Alvys/Writeback</c>) is owned by a separate workstream; Dispatch Assist
/// only records the decision app-side and, when a real writer is registered here, hands it the
/// decision. Until then <see cref="NoOpDispatchAssemblyWriteback"/> is registered and every result
/// is <see cref="DispatchWritebackResult.NotPerformed"/> — never a fabricated booking.
/// </summary>
public interface IDispatchAssemblyWriteback
{
    /// <summary>True only when a real, gated Alvys writer has been wired in.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Attempt to push the assembly to Alvys. The default no-op returns
    /// <see cref="DispatchWritebackResult.NotPerformed"/>; a real implementation must stay behind the
    /// same sandbox/production gating as the rest of the Alvys write boundary.
    /// </summary>
    Task<DispatchWritebackResult> PushAsync(DispatchAssembly assembly, CancellationToken ct);
}

/// <summary>The honest outcome of a writeback attempt.</summary>
public sealed class DispatchWritebackResult
{
    public required string Status { get; init; }
    public string? Detail { get; init; }

    public static DispatchWritebackResult NotPerformed { get; } = new()
    {
        Status = "NotPerformed",
        Detail = "Alvys write client not wired to Dispatch Assist; decision recorded internally only.",
    };
}

/// <summary>
/// Default writeback: records nothing upstream. Keeps Dispatch Assist read-only against Alvys while
/// exposing the exact hook a future write workstream implements. TODO(writeback): register a real
/// <see cref="IDispatchAssemblyWriteback"/> that routes through the gated Alvys /api/alvys/ops
/// trip-assign + trip-dispatch operations once that contract is confirmed and signed off.
/// </summary>
public sealed class NoOpDispatchAssemblyWriteback : IDispatchAssemblyWriteback
{
    public bool IsEnabled => false;

    public Task<DispatchWritebackResult> PushAsync(DispatchAssembly assembly, CancellationToken ct) =>
        Task.FromResult(DispatchWritebackResult.NotPerformed);
}
