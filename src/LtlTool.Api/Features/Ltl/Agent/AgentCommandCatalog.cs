namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Static catalog of the M4 agent commands as tool-style schemas. Kept independent of the handler
/// instances so <c>GET /api/ltl/agent/commands</c> can advertise the surface even when the feature is
/// disabled (the null dispatcher still returns the catalog) and so a future LLM function-calling
/// layer has one authoritative definition to publish. Every command is read-only against Alvys.
/// </summary>
public static class AgentCommandCatalog
{
    public const string ListOpportunities = "list-opportunities";
    public const string ExplainPlan = "explain-plan";
    public const string CheckFit = "check-fit";
    public const string SequenceStops = "sequence-stops";
    public const string EstimateQuote = "estimate-quote";
    public const string ReportIncident = "report-incident";

    public static readonly IReadOnlyList<AgentCommandSchema> All =
    [
        new AgentCommandSchema
        {
            Name = ListOpportunities,
            Description =
                "List ranked consolidation opportunities from live Alvys loads, optionally filtered " +
                "by corridor and lookback window.",
            Parameters =
            [
                new("limit", "integer", false, "Max opportunities to return (1-50, default 10)."),
                new("lookbackDays", "integer", false, "How many days of delivered loads to scan (1-90, default 14)."),
                new("corridor", "string", false, "Corridor code to filter by, e.g. LAREDO_TO_DALLAS."),
            ],
        },
        new AgentCommandSchema
        {
            Name = ExplainPlan,
            Description =
                "Explain a recorded consolidation plan: solver rationale, trailer-fit verdict, and the " +
                "per-sibling Lane/Timing/Customer fit chips.",
            Parameters =
            [
                new("planId", "string", true, "Id of a recorded consolidation audit entry to explain."),
            ],
        },
        new AgentCommandSchema
        {
            Name = CheckFit,
            Description =
                "Check whether a set of loads fits a trailer (by weight/pallets/cube) using the " +
                "trailer-fit engine. Supply a planId or an explicit load list.",
            Parameters =
            [
                new("planId", "string", false, "Recorded plan id whose parent+siblings to check."),
                new("loads", "array", false, "Explicit loads: [{loadRef, weightLbs?, pallets?, volume?}]."),
                new("trailer", "object", false, "Optional trailer capacity: {maxWeightLbs?, maxPallets?, maxVolume?}."),
            ],
        },
        new AgentCommandSchema
        {
            Name = SequenceStops,
            Description =
                "Order a plan's stops into a sensible route via the stop sequencer. Supply a planId or " +
                "an explicit stop list.",
            Parameters =
            [
                new("planId", "string", false, "Recorded plan id whose stops to sequence."),
                new("stops", "array", false, "Explicit stops: [{stopRef, city?, state?, latitude?, longitude?}]."),
            ],
        },
        new AgentCommandSchema
        {
            Name = EstimateQuote,
            Description =
                "Reference-only freight cost/CO₂ estimate for a lane and weight. NOT an Alvys rate — " +
                "a calculator for planning conversations.",
            Parameters =
            [
                new("origin", "string", true, "Origin US state code or name (e.g. TX)."),
                new("destination", "string", true, "Destination US state code or name (e.g. IL)."),
                new("weightLbs", "number", true, "Payload weight in pounds (> 0)."),
                new("mode", "string", false, "Truck (default), Rail, or Air."),
                new("distanceMiles", "number", false, "Known mileage; when omitted a reference distance is estimated."),
                new("perishable", "boolean", false, "Apply the perishable/reefer surcharge."),
                new("hazmat", "boolean", false, "Apply the hazmat surcharge."),
            ],
        },
        new AgentCommandSchema
        {
            Name = ReportIncident,
            Description =
                "Record a corridor incident (in-memory, never written to Alvys) that raises the " +
                "corridor's surge/risk factor used by estimate-quote.",
            Parameters =
            [
                new("origin", "string", true, "Corridor origin token (e.g. TX)."),
                new("destination", "string", true, "Corridor destination token (e.g. IL)."),
                new("severity", "integer", true, "Severity 1 (minor) to 5 (severe)."),
                new("note", "string", false, "Optional operator context."),
            ],
        },
    ];

    /// <summary>Look up a schema by command name, or null when unknown.</summary>
    public static AgentCommandSchema? Get(string command) =>
        All.FirstOrDefault(s => string.Equals(s.Name, command, StringComparison.OrdinalIgnoreCase));
}
