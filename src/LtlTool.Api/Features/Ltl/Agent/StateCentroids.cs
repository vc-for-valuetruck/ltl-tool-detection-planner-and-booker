namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Static US state capital coordinates used only by <see cref="QuoteEstimatorService"/> to compute a
/// great-circle <i>reference</i> distance when the caller supplies no mileage. Ported from the
/// LogisticsRoute <c>STATE_CAPITALS</c> table. These are geographic constants, not Alvys operational
/// data, and the distances they produce are always labeled reference-only.
/// </summary>
internal static class StateCentroids
{
    public readonly record struct Coord(double Lat, double Lon);

    public static readonly IReadOnlyDictionary<string, Coord> Table = new Dictionary<string, Coord>(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = new(32.377716, -86.300568),
        ["AK"] = new(58.301598, -134.420212),
        ["AZ"] = new(33.448143, -112.096962),
        ["AR"] = new(34.746613, -92.288986),
        ["CA"] = new(38.576668, -121.493629),
        ["CO"] = new(39.739227, -104.984856),
        ["CT"] = new(41.764046, -72.682198),
        ["DE"] = new(39.157307, -75.519722),
        ["DC"] = new(38.907192, -77.036873),
        ["FL"] = new(30.438118, -84.281296),
        ["GA"] = new(33.749027, -84.388229),
        ["HI"] = new(21.307442, -157.857376),
        ["ID"] = new(43.617775, -116.199722),
        ["IL"] = new(39.798363, -89.654961),
        ["IN"] = new(39.768623, -86.162643),
        ["IA"] = new(41.591087, -93.603729),
        ["KS"] = new(39.048191, -95.677956),
        ["KY"] = new(38.186722, -84.875374),
        ["LA"] = new(30.457069, -91.187393),
        ["ME"] = new(44.307167, -69.781693),
        ["MD"] = new(38.978764, -76.490936),
        ["MA"] = new(42.358162, -71.063698),
        ["MI"] = new(42.733635, -84.555328),
        ["MN"] = new(44.955097, -93.102211),
        ["MS"] = new(32.303848, -90.182106),
        ["MO"] = new(38.579201, -92.172935),
        ["MT"] = new(46.585709, -112.018417),
        ["NE"] = new(40.808075, -96.699654),
        ["NV"] = new(39.163914, -119.766121),
        ["NH"] = new(43.206898, -71.537994),
        ["NJ"] = new(40.220596, -74.769913),
        ["NM"] = new(35.68224, -105.939728),
        ["NY"] = new(42.652843, -73.757874),
        ["NC"] = new(35.780430, -78.639099),
        ["ND"] = new(46.82085, -100.783318),
        ["OH"] = new(39.961346, -82.999069),
        ["OK"] = new(35.492207, -97.503342),
        ["OR"] = new(44.938461, -123.030403),
        ["PA"] = new(40.264378, -76.883598),
        ["RI"] = new(41.830914, -71.414963),
        ["SC"] = new(34.000343, -81.033211),
        ["SD"] = new(44.367031, -100.346405),
        ["TN"] = new(36.16581, -86.784241),
        ["TX"] = new(30.27467, -97.740349),
        ["UT"] = new(40.777477, -111.888237),
        ["VT"] = new(44.262436, -72.580536),
        ["VA"] = new(37.538857, -77.43364),
        ["WA"] = new(47.035805, -122.905014),
        ["WV"] = new(38.336246, -81.612328),
        ["WI"] = new(43.074684, -89.384445),
        ["WY"] = new(41.140259, -104.820236),
    };

    /// <summary>Full-state-name (upper-cased) → two-letter code, for lenient input parsing.</summary>
    public static readonly IReadOnlyDictionary<string, string> NameToCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ALABAMA"] = "AL", ["ALASKA"] = "AK", ["ARIZONA"] = "AZ", ["ARKANSAS"] = "AR",
        ["CALIFORNIA"] = "CA", ["COLORADO"] = "CO", ["CONNECTICUT"] = "CT", ["DELAWARE"] = "DE",
        ["DISTRICT OF COLUMBIA"] = "DC", ["FLORIDA"] = "FL", ["GEORGIA"] = "GA", ["HAWAII"] = "HI",
        ["IDAHO"] = "ID", ["ILLINOIS"] = "IL", ["INDIANA"] = "IN", ["IOWA"] = "IA",
        ["KANSAS"] = "KS", ["KENTUCKY"] = "KY", ["LOUISIANA"] = "LA", ["MAINE"] = "ME",
        ["MARYLAND"] = "MD", ["MASSACHUSETTS"] = "MA", ["MICHIGAN"] = "MI", ["MINNESOTA"] = "MN",
        ["MISSISSIPPI"] = "MS", ["MISSOURI"] = "MO", ["MONTANA"] = "MT", ["NEBRASKA"] = "NE",
        ["NEVADA"] = "NV", ["NEW HAMPSHIRE"] = "NH", ["NEW JERSEY"] = "NJ", ["NEW MEXICO"] = "NM",
        ["NEW YORK"] = "NY", ["NORTH CAROLINA"] = "NC", ["NORTH DAKOTA"] = "ND", ["OHIO"] = "OH",
        ["OKLAHOMA"] = "OK", ["OREGON"] = "OR", ["PENNSYLVANIA"] = "PA", ["RHODE ISLAND"] = "RI",
        ["SOUTH CAROLINA"] = "SC", ["SOUTH DAKOTA"] = "SD", ["TENNESSEE"] = "TN", ["TEXAS"] = "TX",
        ["UTAH"] = "UT", ["VERMONT"] = "VT", ["VIRGINIA"] = "VA", ["WASHINGTON"] = "WA",
        ["WEST VIRGINIA"] = "WV", ["WISCONSIN"] = "WI", ["WYOMING"] = "WY",
    };
}
