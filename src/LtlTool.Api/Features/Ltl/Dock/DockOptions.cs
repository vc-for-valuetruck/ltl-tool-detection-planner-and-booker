namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Dock mode (Phase 2.5) configuration, bound from <c>Ltl:Dock</c>. Today it carries only the
/// per-warehouse combine-notification recipient lists. Ships empty by default, which means dock
/// notifications are disabled out of the box — a fresh clone / CI / the demo never attempt to email
/// anyone. Populate <see cref="NotifyRecipients"/> per warehouse code to turn combine notifications
/// on for that yard. The actual send still only happens when the shared email channel is itself
/// configured (<c>Notifications:Email</c>); until then the send reports an honest Pending/NotConfigured
/// state rather than a fabricated delivery.
/// </summary>
public sealed class DockOptions
{
    public const string SectionName = "Ltl:Dock";

    /// <summary>
    /// Email recipients to notify when a combine is committed at a given yard, keyed by warehouse
    /// code (e.g. <c>LAREDO</c>). Empty (the default) means "no one configured" → notifications are
    /// disabled for that yard. Addresses are server-side config only and never returned to the SPA.
    /// </summary>
    public Dictionary<string, List<string>> NotifyRecipients { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recipients configured for a warehouse, trimmed of blanks; empty when none.</summary>
    public IReadOnlyList<string> RecipientsFor(string? warehouseCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode) ||
            !NotifyRecipients.TryGetValue(warehouseCode.Trim(), out var list) || list is null)
        {
            return [];
        }

        return list
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToArray();
    }
}
