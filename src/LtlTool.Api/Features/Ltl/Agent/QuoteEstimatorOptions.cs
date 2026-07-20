namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Reference rate card for <see cref="QuoteEstimatorService"/>, bound from
/// <c>Ltl:Optimization:Quote</c>. Defaults are a defensible dry-van reference card ported in spirit
/// from the LogisticsRoute pricing model; they are tuning knobs, not Alvys data. Changing them only
/// changes reference estimates, never anything operational.
/// </summary>
public sealed class QuoteEstimatorOptions
{
    public const string SectionName = "Ltl:Optimization:Quote";

    /// <summary>Truck (dry van) rate row — the default mode.</summary>
    public QuoteModeRate Truck { get; set; } = new()
    {
        BasePerMile = 1.75m,
        PerTonMile = 0.12m,
        Co2KgPerTonMile = 0.1618m,
    };

    /// <summary>Rail rate row — cheaper per ton-mile, far lower CO₂.</summary>
    public QuoteModeRate Rail { get; set; } = new()
    {
        BasePerMile = 0.90m,
        PerTonMile = 0.05m,
        Co2KgPerTonMile = 0.0252m,
    };

    /// <summary>Air rate row — premium per ton-mile, high CO₂.</summary>
    public QuoteModeRate Air { get; set; } = new()
    {
        BasePerMile = 6.50m,
        PerTonMile = 0.90m,
        Co2KgPerTonMile = 0.6600m,
    };

    /// <summary>Fuel surcharge as a fraction of linehaul.</summary>
    public decimal FuelSurchargePct { get; set; } = 0.28m;

    /// <summary>Flat handling fee applied to heavy freight above <see cref="HandlingThresholdLbs"/>.</summary>
    public decimal HandlingFee { get; set; } = 45.00m;

    /// <summary>Weight (lbs) above which the handling fee applies (LogisticsRoute's 1000 kg cut-over).</summary>
    public decimal HandlingThresholdLbs { get; set; } = 2205m;

    /// <summary>Additive surcharge when the freight is perishable/reefer.</summary>
    public decimal PerishableSurcharge { get; set; } = 120.00m;

    /// <summary>Additive surcharge when the freight is hazmat.</summary>
    public decimal HazmatSurcharge { get; set; } = 250.00m;

    /// <summary>Insurance as a fraction of the surged subtotal.</summary>
    public decimal InsurancePct { get; set; } = 0.02m;

    /// <summary>
    /// Per-destination-state congestion multiplier (applied additively over linehaul as
    /// multiplier − 1). Any state not listed uses 1.0 (no premium). Defaults cover the most
    /// congestion-prone lanes; tune per environment.
    /// </summary>
    public Dictionary<string, decimal> CongestionMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CA"] = 1.08m,
        ["NY"] = 1.10m,
        ["IL"] = 1.06m,
        ["NJ"] = 1.07m,
    };
}

/// <summary>One transport mode's reference rate row.</summary>
public sealed class QuoteModeRate
{
    /// <summary>Flat cost per loaded mile.</summary>
    public decimal BasePerMile { get; set; }

    /// <summary>Additional cost per mile per ton of payload.</summary>
    public decimal PerTonMile { get; set; }

    /// <summary>Reference CO₂ emitted per ton-mile (kg).</summary>
    public decimal Co2KgPerTonMile { get; set; }
}
