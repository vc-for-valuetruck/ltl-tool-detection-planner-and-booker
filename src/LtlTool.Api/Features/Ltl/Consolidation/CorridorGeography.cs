namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Single source of truth for "is this place near this corridor warehouse" — used by both
/// <see cref="ConsolidationCandidateService"/> (Auto-suggest / candidate listing) and
/// <see cref="ConsolidationPlanService"/> (Dock review / combine). Both call sites must agree on
/// this evaluation; a prior drift (candidate service fixed in #100, plan service left stale) let
/// loads surface as suggested siblings that then failed the Review/Combine gate with a
/// contradictory "not on the corridor" blocker for freight that was, in fact, on the corridor.
/// </summary>
public static class CorridorGeography
{
    /// <summary>
    /// True when <paramref name="place"/> is close enough to <paramref name="warehouse"/> to count
    /// toward the corridor.
    ///
    /// <para>
    /// An explicit nearby-city hit counts regardless of state: the corridor's <c>NearbyCities</c>
    /// list is a curated whitelist and legitimately spans the border (e.g. the LAREDO yard lists
    /// the Monterrey-area cluster — Santa Catarina, NL — whose freight physically crosses at
    /// Laredo). Requiring state equality first would structurally exclude that freight
    /// (NL != TX) and make the cross-border whitelist entries unreachable.
    /// </para>
    /// <para>
    /// Same-state is a fallback for in-state freight with no explicit city hit: an empty
    /// <c>NearbyCities</c> list means "whole state matches", and a blank candidate city is given
    /// the benefit of the doubt within the state rather than excluded on missing data.
    /// </para>
    /// </summary>
    public static bool IsNear(LtlPlace? place, ConsolidationWarehouseOptions warehouse)
    {
        if (place is null) return false;

        if (!string.IsNullOrWhiteSpace(place.City))
        {
            foreach (var city in warehouse.NearbyCities)
            {
                if (place.City.Contains(city, StringComparison.OrdinalIgnoreCase)) return true;
                if (city.Contains(place.City, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(place.State)
            && string.Equals(place.State, warehouse.State, StringComparison.OrdinalIgnoreCase))
        {
            if (warehouse.NearbyCities.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(place.City)) return true;
        }

        return false;
    }
}
