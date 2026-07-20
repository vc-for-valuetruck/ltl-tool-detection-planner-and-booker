using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Reference-only freight quote estimator. The pricing / CO₂ / surcharge stack is ported from the
/// MIT-licensed LogisticsRoute agent (<c>calculate_pricing_and_co2</c>) and re-expressed as pure,
/// deterministic C# arithmetic. It exists so the agent command surface can answer "roughly what
/// would this move cost / emit?" for planning conversations.
///
/// <para>
/// <b>Guardrail — this is NOT Alvys data.</b> Every number returned here is a reference estimate
/// derived from a static rate card and (when no caller-supplied distance is available) a great-circle
/// distance between US state centroids. It must never be presented as an Alvys rate, an Alvys mileage,
/// or a booked price. The response carries <see cref="QuoteEstimate.Disclaimer"/> and
/// <see cref="QuoteEstimate.DistanceSource"/> so the UI/LLM always labels it as an estimate. This
/// respects the Alvys-only-source-of-truth rule: nothing here feeds an operational decision or is
/// written back; it is a calculator, clearly fenced off from live data.
/// </para>
/// </summary>
public sealed class QuoteEstimatorService(IOptions<QuoteEstimatorOptions> options)
{
    private readonly QuoteEstimatorOptions _opts = options.Value;

    /// <summary>
    /// Produce a reference quote. <paramref name="surgeMultiplier"/> is applied to the subtotal and
    /// defaults to 1.0 (no surge); the caller passes an incident-derived surge from
    /// <see cref="IncidentStore"/> when it wants a risk-adjusted number. The pricing math is fully
    /// deterministic given the inputs, so it is golden-testable independent of the incident store.
    /// </summary>
    public QuoteEstimate Estimate(QuoteEstimateInput input, double surgeMultiplier = 1.0)
    {
        if (input.WeightLbs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Weight must be positive.");
        }

        var mode = input.Mode ?? QuoteTransportMode.Truck;

        // Distance: prefer the caller-supplied value (which the agent would source from Alvys mileage
        // when it has it); otherwise fall back to a clearly-labeled great-circle reference estimate.
        decimal distanceMiles;
        string distanceSource;
        if (input.DistanceMiles is > 0)
        {
            distanceMiles = input.DistanceMiles.Value;
            distanceSource = "caller-supplied";
        }
        else
        {
            var reference = ReferenceDistanceMiles(input.Origin, input.Destination);
            if (reference is null)
            {
                throw new ArgumentException(
                    "Cannot estimate a reference distance: origin/destination must be US state codes " +
                    "(or a caller-supplied distanceMiles).", nameof(input));
            }
            distanceMiles = decimal.Round(reference.Value, 1);
            distanceSource = "reference-haversine-estimate";
        }

        var weightTons = input.WeightLbs / 2000m;

        // Linehaul = distance × (flat per-mile + per-ton-mile × tons). Mode picks the rate row.
        var rate = RateFor(mode);
        var linehaul = distanceMiles * (rate.BasePerMile + rate.PerTonMile * weightTons);

        var fuelSurcharge = linehaul * _opts.FuelSurchargePct;

        // Handling fee applies above the heavy-freight threshold (LogisticsRoute used a 1000 kg
        // cut-over; expressed here in lbs for the US fleet).
        var handlingFee = input.WeightLbs > _opts.HandlingThresholdLbs ? _opts.HandlingFee : 0m;

        var accessorials = 0m;
        if (input.Perishable) accessorials += _opts.PerishableSurcharge;
        if (input.Hazmat) accessorials += _opts.HazmatSurcharge;

        // Destination congestion premium: additive over linehaul (multiplier − 1), default 1.0.
        var congestionMultiplier = CongestionMultiplier(input.Destination);
        var congestionPremium = linehaul * (congestionMultiplier - 1m);

        var subtotal = linehaul + fuelSurcharge + handlingFee + accessorials + congestionPremium;

        // Surge from corridor incidents (>= 1.0). Clamp defensively.
        var surge = (decimal)Math.Max(1.0, surgeMultiplier);
        var surged = subtotal * surge;

        var insurance = surged * _opts.InsurancePct;
        var total = surged + insurance;

        var co2Kg = weightTons * distanceMiles * rate.Co2KgPerTonMile;

        return new QuoteEstimate
        {
            Origin = input.Origin,
            Destination = input.Destination,
            Mode = mode,
            WeightLbs = decimal.Round(input.WeightLbs, 2),
            DistanceMiles = distanceMiles,
            DistanceSource = distanceSource,
            Linehaul = decimal.Round(linehaul, 2),
            FuelSurcharge = decimal.Round(fuelSurcharge, 2),
            HandlingFee = decimal.Round(handlingFee, 2),
            AccessorialSurcharge = decimal.Round(accessorials, 2),
            CongestionPremium = decimal.Round(congestionPremium, 2),
            SurgeMultiplier = decimal.Round(surge, 3),
            Insurance = decimal.Round(insurance, 2),
            TotalCost = decimal.Round(total, 2),
            Co2Kg = decimal.Round(co2Kg, 2),
        };
    }

    private QuoteModeRate RateFor(QuoteTransportMode mode) => mode switch
    {
        QuoteTransportMode.Rail => _opts.Rail,
        QuoteTransportMode.Air => _opts.Air,
        _ => _opts.Truck,
    };

    private decimal CongestionMultiplier(string? destination)
    {
        var state = NormalizeState(destination);
        if (state is not null && _opts.CongestionMultipliers.TryGetValue(state, out var m))
        {
            return m;
        }
        return 1.0m;
    }

    /// <summary>
    /// Great-circle distance (miles) between two US state centroids, or null when either side can't
    /// be resolved to a known state. Reference-only; never an Alvys mileage.
    /// </summary>
    public decimal? ReferenceDistanceMiles(string? origin, string? destination)
    {
        var o = NormalizeState(origin);
        var d = NormalizeState(destination);
        if (o is null || d is null) return null;
        if (!StateCentroids.Table.TryGetValue(o, out var a)) return null;
        if (!StateCentroids.Table.TryGetValue(d, out var b)) return null;
        return (decimal)Haversine(a.Lat, a.Lon, b.Lat, b.Lon);
    }

    /// <summary>Map an input string to a two-letter state code (accepts "TX", "tx", "Texas").</summary>
    private static string? NormalizeState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 2 && StateCentroids.Table.ContainsKey(trimmed.ToUpperInvariant()))
        {
            return trimmed.ToUpperInvariant();
        }
        return StateCentroids.NameToCode.TryGetValue(trimmed.ToUpperInvariant(), out var code)
            ? code
            : null;
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMiles = 3958.7613;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMiles * c;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
