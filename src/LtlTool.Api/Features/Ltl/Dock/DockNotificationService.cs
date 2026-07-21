using System.Globalization;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Emails a dock combine summary (parent, children, combined RPM, docs reference) to the recipients
/// configured for the yard in <see cref="DockOptions.NotifyRecipients"/>, reusing the shared
/// <see cref="INotificationChannel"/> email transport and recording the event in the in-app feed so
/// dispatch/leadership see it alongside every other workflow notification.
///
/// <para>
/// Failure never blocks the dock worker: every path is wrapped so the combine result surfaces an
/// honest <see cref="DockNotificationResult"/> the SPA renders as a retry chip when it did not land.
/// No delivery is ever fabricated — an unconfigured email channel reports NotConfigured/Pending, a
/// transport error reports Failed, and "no recipients configured for this yard" reports Disabled.
/// Read-only against Alvys — this only reads config + sends mail.
/// </para>
/// </summary>
public sealed class DockNotificationService(
    IEnumerable<INotificationChannel> channels,
    INotificationStore store,
    IOptions<DockOptions> options,
    TimeProvider clock,
    ILogger<DockNotificationService> logger)
{
    private readonly IReadOnlyList<INotificationChannel> _channels = channels.ToArray();
    private readonly DockOptions _options = options.Value;

    /// <summary>
    /// Notifies the yard's recipients that a combine was committed. Never throws — returns the honest
    /// outcome so the caller can keep the dock worker moving and offer a retry.
    /// </summary>
    public async Task<DockNotificationResult> NotifyCombineAsync(
        string? warehouseCode, ConsolidationPlanResponse plan, CancellationToken ct)
    {
        var addresses = _options.RecipientsFor(warehouseCode);
        if (addresses.Count == 0)
        {
            return new DockNotificationResult
            {
                State = "Disabled",
                Recipients = [],
                Detail = "No dock notification recipients configured for this yard (Ltl:Dock:NotifyRecipients).",
            };
        }

        try
        {
            var evt = BuildEvent(warehouseCode, plan);
            var recipients = addresses
                .Select(a => new NotificationRecipient
                {
                    Name = a,
                    Channel = NotificationChannelKind.Email,
                    Address = a,
                })
                .ToArray();

            // In-app feed record so the combine is visible next to every other workflow event.
            store.TryAdd(evt);

            var email = _channels.FirstOrDefault(c => c.Kind == NotificationChannelKind.Email);
            if (email is null)
            {
                return new DockNotificationResult
                {
                    State = "Failed",
                    Recipients = addresses,
                    Detail = "No email channel registered.",
                };
            }

            var delivery = await email.SendAsync(evt, recipients, ct);
            return new DockNotificationResult
            {
                State = delivery.State.ToString(),
                Recipients = addresses,
                Detail = delivery.Detail,
            };
        }
        catch (Exception ex)
        {
            // Non-blocking: the combine already succeeded and the audit is recorded. A notify failure
            // is logged and surfaced as a retryable chip, never propagated to the dock worker.
            logger.LogWarning(ex, "Dock combine notification failed for parent {ParentLoadId}.", plan.Parent.Id);
            return new DockNotificationResult
            {
                State = "Failed",
                Recipients = addresses,
                Detail = "Notification send failed; retry available.",
            };
        }
    }

    private NotificationEvent BuildEvent(string? warehouseCode, ConsolidationPlanResponse plan)
    {
        var parentLabel = plan.Parent.LoadNumber ?? plan.Parent.Id;
        var children = plan.Siblings.Select(s => s.LoadNumber ?? s.LoadId).ToArray();
        var rpm = plan.CombinedRevenuePerMile is { } r
            ? "$" + r.ToString("0.00", CultureInfo.InvariantCulture) + " / mi"
            : "—";
        var docs = string.IsNullOrWhiteSpace(plan.ClickCard.TripReferenceValue)
            ? "—"
            : $"{plan.ClickCard.TripReferenceValue} / Main Load Id={plan.ClickCard.MainLoadIdReferenceValue}";

        var childList = children.Length > 0 ? string.Join(", ", children) : "none";
        var summary =
            $"Combined at {warehouseCode ?? "yard"}: parent {parentLabel} (BOL controlling) + " +
            $"{children.Length} child load(s) [{childList}]. Combined driver RPM {rpm}. Docs: {docs}.";

        var now = clock.GetUtcNow();
        var key = $"DockCombine:{plan.PreviewId}";
        return new NotificationEvent
        {
            Id = Guid.NewGuid().ToString("n"),
            IdempotencyKey = key,
            Stage = NotificationStage.ClickCardGenerated,
            Title = $"Dock combine committed — parent {parentLabel}",
            Summary = summary,
            LoadId = plan.Parent.Id,
            LoadNumber = plan.Parent.LoadNumber,
            PlanId = plan.PreviewId,
            LinkPath = "/ltl/dock",
            OccurredAt = now,
            FiredAt = now,
            Deliveries = [],
        };
    }
}
