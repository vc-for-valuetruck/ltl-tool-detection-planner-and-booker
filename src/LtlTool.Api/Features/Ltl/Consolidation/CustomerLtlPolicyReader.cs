using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Resolves a customer's LTL consolidation tier. Two-layer resolution: first checks the
/// Alvys customer's <c>Notes[]</c> for an <c>LTL_TIER=…</c> or <c>LTL_ALLOW=…</c> line
/// (Reuben-suggested convention, 2026-07-17 sync at 30:51 \u2014 Alvys has no first-class
/// customer flag today); falls back to the static config
/// (<see cref="ConsolidationOptions.CustomerPolicies"/>).
///
/// <para>
/// Never silent-allows. When both sources are absent, returns
/// <see cref="CustomerConsolidationTier.Unknown"/> \u2014 the caller then defaults to
/// \"confirm with account owner\" per the yard-visit guidance.
/// </para>
/// </summary>
public interface ICustomerLtlPolicyReader
{
    /// <summary>Resolve tier + source for a customer, given whatever identifiers we know from the load.</summary>
    Task<CustomerPolicyResolution> ResolveAsync(
        string? customerId,
        string? customerName,
        CancellationToken ct);
}

/// <summary>Where a resolved consolidation tier came from — so the UI can badge it honestly.</summary>
public enum CustomerPolicySource
{
    /// <summary>No policy found in either the customer's Alvys notes or static config.</summary>
    None = 0,

    /// <summary>Resolved from an <c>LTL_TIER=</c>/<c>LTL_ALLOW=</c> line in the Alvys customer notes.</summary>
    CustomerNote = 1,

    /// <summary>Resolved from the static <see cref="ConsolidationOptions.CustomerPolicies"/> fallback.</summary>
    DefaultPolicy = 2,
}

/// <summary>
/// A resolved consolidation tier together with its provenance. The tier drives the Never blocker;
/// the source lets the UI distinguish "from customer note" from "default policy — no customer note"
/// so a dispatcher knows whether the policy is customer-authored or a static fallback.
/// </summary>
public sealed record CustomerPolicyResolution(
    CustomerConsolidationTier Tier,
    CustomerPolicySource Source)
{
    /// <summary>No tier and no source — nothing on file anywhere.</summary>
    public static readonly CustomerPolicyResolution Unknown =
        new(CustomerConsolidationTier.Unknown, CustomerPolicySource.None);
}

/// <summary>
/// Alvys-notes-backed policy reader. Reads the customer record via
/// <see cref="IAlvysClient.SearchCustomersAsync"/>, parses the notes for LTL_* lines, and
/// falls back to the static config when no LTL note is present or the customer lookup
/// degrades.
///
/// <para>
/// Notes convention (documented in <c>docs/ALVYS_API_DECISIONS.md</c> decision #10):
/// </para>
/// <code>
/// LTL_ALLOW=true
/// LTL_TIER=Allowed | NotifyRequired | Never
/// LTL_NOTIFY=alice@company.com
/// </code>
/// <para>
/// Case-insensitive on both keys and values. Lines can appear anywhere in the free-form
/// note text; other lines are ignored. <c>LTL_TIER</c> wins over <c>LTL_ALLOW</c> when
/// both are present. An unrecognised <c>LTL_TIER=</c> value is treated as
/// <see cref="CustomerConsolidationTier.Unknown"/> (never silently promoted).
/// </para>
///
/// <para>
/// Caching: per-name and per-id, in-memory, no TTL. This is a Phase 1 pilot expedient \u2014
/// the process restarts every deploy and the LTL flag is not expected to change
/// mid-pilot. When Alvys ships a first-class customer flag (Reuben confirmed this is on
/// their roadmap), swap the implementation via DI without touching callers.
/// </para>
/// </summary>
public sealed class CustomerNotesLtlPolicyReader(
    IAlvysClient alvys,
    IOptions<ConsolidationOptions> options,
    ILogger<CustomerNotesLtlPolicyReader> logger) : ICustomerLtlPolicyReader
{
    private readonly IAlvysClient _alvys = alvys;
    private readonly ConsolidationOptions _opts = options.Value;
    private readonly ILogger<CustomerNotesLtlPolicyReader> _logger = logger;
    private readonly ConcurrentDictionary<string, CustomerPolicyResolution> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Matches an <c>LTL_TIER=…</c> line anywhere in note text. Case-insensitive.</summary>
    private static readonly Regex TierRegex = new(
        @"\bLTL_TIER\s*=\s*(Allowed|NotifyRequired|Never)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches <c>LTL_ALLOW=true|false</c> \u2014 legacy shorthand form.</summary>
    private static readonly Regex AllowRegex = new(
        @"\bLTL_ALLOW\s*=\s*(true|false)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<CustomerPolicyResolution> ResolveAsync(
        string? customerId,
        string? customerName,
        CancellationToken ct)
    {
        // If we have neither an id nor a name we can't look anything up. Static config keys on
        // name only, so with no name we're at Unknown.
        if (string.IsNullOrWhiteSpace(customerId) && string.IsNullOrWhiteSpace(customerName))
            return CustomerPolicyResolution.Unknown;

        // Cache key: prefer id (stable), fall back to name. Different customers with same name
        // will share a slot; that's acceptable given the Phase 1 pilot scale and the fact that
        // static-config policies are also name-keyed.
        var cacheKey = !string.IsNullOrWhiteSpace(customerId)
            ? $"id:{customerId}"
            : $"name:{customerName!.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var notesTier = await TryReadNotesTierAsync(customerId, customerName, ct);
        if (notesTier is not null)
        {
            var fromNote = new CustomerPolicyResolution(notesTier.Value, CustomerPolicySource.CustomerNote);
            _cache[cacheKey] = fromNote;
            return fromNote;
        }

        // Fall back to static config \u2014 the ConsolidationOptions.CustomerPolicies list.
        var staticResolution = ResolveStaticResolution(customerName);
        _cache[cacheKey] = staticResolution;
        return staticResolution;
    }

    private async Task<CustomerConsolidationTier?> TryReadNotesTierAsync(
        string? customerId,
        string? customerName,
        CancellationToken ct)
    {
        try
        {
            // The Public API's customers/search takes a status filter and returns all matches;
            // there's no name filter, so we page and match client-side. For Phase 1 pilot volume
            // (a few hundred active customers), one paged search is cheap. When this becomes a
            // hot path we can swap to customers_get_by_id via a direct id lookup.
            var response = await _alvys.SearchCustomersAsync(
                new CustomerSearchRequest { Page = 0, PageSize = 500 }, ct);

            var customer = FindCustomer(response.Items, customerId, customerName);
            if (customer?.Notes is null or { Count: 0 }) return null;

            return ParseTierFromNotes(customer.Notes);
        }
        catch (Exception ex)
        {
            // Never fail the plan preview on a customer-lookup degrade \u2014 fall back to static
            // config. Log at debug so ops can see the pattern without alerting.
            _logger.LogDebug(
                ex, "Customer LTL notes read failed for id={CustomerId} name={CustomerName}; falling back to static config.",
                customerId, customerName);
            return null;
        }
    }

    private static AlvysCustomer? FindCustomer(
        IReadOnlyList<AlvysCustomer> customers,
        string? id,
        string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = customers.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            return customers.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>
    /// Parse the first LTL_TIER line we find. Falls through to LTL_ALLOW=true \u2192 Allowed and
    /// LTL_ALLOW=false \u2192 Never when only the shorthand is present. Returns null when no
    /// LTL_* line is present (caller then falls back to static config).
    /// </summary>
    public static CustomerConsolidationTier? ParseTierFromNotes(IReadOnlyList<AlvysContextNote> notes)
    {
        foreach (var note in notes)
        {
            var text = note.Description;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var tierMatch = TierRegex.Match(text);
            if (tierMatch.Success)
            {
                return tierMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "allowed"        => CustomerConsolidationTier.Allowed,
                    "notifyrequired" => CustomerConsolidationTier.NotifyRequired,
                    "never"          => CustomerConsolidationTier.Never,
                    _                => CustomerConsolidationTier.Unknown, // future-proofing
                };
            }
        }

        // No LTL_TIER; look for LTL_ALLOW shorthand.
        foreach (var note in notes)
        {
            var text = note.Description;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var allowMatch = AllowRegex.Match(text);
            if (allowMatch.Success)
            {
                return string.Equals(allowMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase)
                    ? CustomerConsolidationTier.Allowed
                    : CustomerConsolidationTier.Never;
            }
        }

        return null;
    }

    private CustomerPolicyResolution ResolveStaticResolution(string? customerName)
    {
        if (string.IsNullOrWhiteSpace(customerName)) return CustomerPolicyResolution.Unknown;
        var policy = _opts.CustomerPolicies.FirstOrDefault(
            p => string.Equals(p.Customer, customerName, StringComparison.OrdinalIgnoreCase));
        return policy is null
            ? CustomerPolicyResolution.Unknown
            : new CustomerPolicyResolution(policy.Tier, CustomerPolicySource.DefaultPolicy);
    }
}
