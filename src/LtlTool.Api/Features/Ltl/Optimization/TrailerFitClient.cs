using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// Typed client for the trailer-fit packing sidecar (services/trailer-fit). Hand-rolled DTOs mirror
/// the sidecar's <c>POST /optimize-load</c> contract (services/trailer-fit/openapi.json) — kept
/// dependency-light rather than pulling in a codegen toolchain. The sidecar is a pure compute
/// function: this client sends shipment geometry (inches + pounds) and receives a load-plan summary.
/// It sends <b>no</b> Alvys identifiers or customer data — only dimensions/weights the API already
/// holds — so "Alvys is the only source of truth" is preserved.
/// </summary>
public interface ITrailerFitClient
{
    /// <summary>
    /// Calls the sidecar. Returns the parsed summary on HTTP 200; returns <c>null</c> for any
    /// non-success status or transport/timeout failure so the caller can degrade honestly.
    /// The supplied <paramref name="ct"/> carries the caller's timeout.
    /// </summary>
    Task<TrailerFitPlanSummary?> OptimizeAsync(TrailerFitOptimizeRequest request, CancellationToken ct);
}

/// <summary>Default <see cref="ITrailerFitClient"/> over a named <see cref="HttpClient"/> ("TrailerFit").</summary>
public sealed class HttpTrailerFitClient(HttpClient http) : ITrailerFitClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<TrailerFitPlanSummary?> OptimizeAsync(
        TrailerFitOptimizeRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("optimize-load", request, JsonOptions, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content
            .ReadFromJsonAsync<TrailerFitPlanSummary>(JsonOptions, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Request body for the sidecar's <c>POST /optimize-load</c>. Serialized snake_case.</summary>
public sealed record TrailerFitOptimizeRequest
{
    public required IReadOnlyList<TrailerFitShipmentItem> ShipmentList { get; init; }

    /// <summary>Standard equipment code for the target trailer, e.g. <c>DV_53</c>.</summary>
    public string? EquipmentCode { get; init; }

    /// <summary>Fixed RNG seed so the same plan is reproducible for a given load set.</summary>
    public int? Seed { get; init; }

    /// <summary>Allow 90° rotation in the length×width plane.</summary>
    public bool AllowRotations { get; init; } = true;
}

/// <summary>A single shipment line sent to the sidecar. Dimensions in inches, weight in pounds.</summary>
public sealed record TrailerFitShipmentItem
{
    public required decimal Length { get; init; }
    public required decimal Width { get; init; }
    public required decimal Height { get; init; }
    public required decimal Weight { get; init; }

    /// <summary><c>PALLET</c> or <c>BOX</c>.</summary>
    public required string Packing { get; init; }

    /// <summary>Max pieces in a vertical stack; 1 = not stackable.</summary>
    public required int StackLimit { get; init; }

    public int NumPieces { get; init; } = 1;

    /// <summary>Passthrough identifier — the source load ref, never customer PII.</summary>
    public string? Id { get; init; }
}

/// <summary>
/// Subset of the sidecar's <c>trailer.get_summary()</c> response the tool consumes. Extra fields the
/// sidecar returns (per-piece <c>load_order</c>, additional utilization ratios) are ignored.
/// </summary>
public sealed record TrailerFitPlanSummary
{
    public bool ArrangementIsValid { get; init; }
    public bool TrailerIsOverweight { get; init; }
    public decimal? LinearFeet { get; init; }
    public decimal? TotalWeight { get; init; }
    public decimal? TrailerMaxWeight { get; init; }
    public int NumPieces { get; init; }
    public decimal? WeightPortionOfTrailer { get; init; }
    public decimal? StackedCubePortionOfTrailer { get; init; }
    public decimal? LinearFeetPortionOfTrailer { get; init; }
}
