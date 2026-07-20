using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// Real <see cref="ITrailerFitService"/>: turns Alvys-derived load aggregates into a shipment
/// geometry the packing sidecar can evaluate, calls the sidecar, and maps its summary into an
/// explainable <see cref="TrailerFitResult"/>. Registered only when
/// <c>Ltl:Optimization:TrailerFit:Enabled = true</c>; otherwise <see cref="NullTrailerFitService"/>
/// stands in.
///
/// <para>
/// <b>Degraded (estimated) inputs.</b> Alvys carries only aggregate weight / volume / pallets per
/// load — never true per-item L×W×H (the load is always flagged <see cref="MissingDataFlag.Dimensions"/>).
/// So each load is expanded into standard 48×40 pallets: pallet count from the load's pallet signal,
/// else derived from volume, else from weight; per-piece height derived from volume, else a standard
/// pallet height; <c>stack_limit = 1</c> (no stacking assumed without real dims). A fixed seed derived
/// from the load refs makes the plan reproducible. Results are labelled <see cref="TrailerFitResult.EstimatedFit"/>.
/// </para>
///
/// <para>
/// <b>Resilience.</b> A timeout or any transport failure never fails the caller's request — the
/// service degrades to an <c>Unknown</c> verdict ("verify at dock"). Capacity arithmetic over the
/// real Alvys weights still runs, so a plan already over the trailer maximum is reported as
/// <c>DoesNotFit</c> even when the packer is unreachable.
/// </para>
/// </summary>
public sealed class HttpTrailerFitService(
    ITrailerFitClient client,
    IOptions<TrailerFitOptions> options,
    TimeProvider timeProvider,
    ILogger<HttpTrailerFitService> logger) : ITrailerFitService
{
    private const decimal PalletLengthInches = 48m;
    private const decimal PalletWidthInches = 40m;
    private const decimal CubicInchesPerCubicFoot = 1728m;
    private const decimal PalletFootprintSqInches = PalletLengthInches * PalletWidthInches;

    private readonly TrailerFitOptions _opts = options.Value;

    public bool IsEnabled => true;

    public async Task<TrailerFitResult> EvaluateAsync(TrailerFitRequest request, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();

        var maxWeight = request.Trailer.MaxWeightLbs ?? _opts.StandardTrailerMaxWeightLbs;
        var maxPallets = request.Trailer.MaxPallets ?? _opts.StandardTrailerMaxPallets;

        // Capacity arithmetic over the real Alvys values — computed regardless of the packer so an
        // already-overweight plan still warns even if the sidecar is unreachable.
        var weightUnknown = request.Items.Any(i => i.WeightLbs is null or <= 0);
        var totalWeight = request.Items.Sum(i => i.WeightLbs is > 0 ? i.WeightLbs.Value : 0m);
        var (derivedItems, totalPallets) = DeriveShipmentItems(request.Items);
        var weightExceeded = totalWeight > maxWeight;               // floor-exceeds when weightUnknown
        var palletsExceeded = totalPallets is int p && p > maxPallets;
        var capacityExceeded = weightExceeded || palletsExceeded;

        // Base result carrying the pure-arithmetic capacity fields; populated further on packer success.
        var baseResult = new TrailerFitResult(TrailerFitVerdict.Unknown, "", now)
        {
            TotalWeightLbs = totalWeight > 0 ? totalWeight : null,
            TrailerMaxWeightLbs = maxWeight,
            TotalPallets = totalPallets,
            TrailerMaxPallets = maxPallets,
            WeightUnknown = weightUnknown,
            WeightUtilization = maxWeight > 0 && totalWeight > 0 && !weightUnknown
                ? Math.Round(totalWeight / maxWeight, 3)
                : null,
            CapacityExceeded = capacityExceeded,
        };

        TrailerFitPlanSummary? summary = null;
        if (derivedItems.Count > 0)
        {
            var optimizeRequest = new TrailerFitOptimizeRequest
            {
                ShipmentList = derivedItems,
                EquipmentCode = _opts.EquipmentCode,
                Seed = DeriveSeed(request.Items),
            };
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _opts.TimeoutSeconds)));
                summary = await client.OptimizeAsync(optimizeRequest, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Timeout (linked-token cancel surfaces as OperationCanceledException) or transport
                // failure. Never fail the request — degrade below. Re-throw only genuine caller cancel.
                logger.LogWarning(ex, "Trailer-fit sidecar call failed; degrading to verify-at-dock.");
                summary = null;
            }
        }

        if (summary is null)
        {
            // Packer unavailable. If the real weight/pallets already exceed capacity that is a firm
            // DoesNotFit; otherwise we honestly cannot say — Unknown, "verify at dock".
            return capacityExceeded
                ? baseResult with
                {
                    Verdict = TrailerFitVerdict.DoesNotFit,
                    Rationale = BuildCapacityRationale(totalWeight, maxWeight, totalPallets, maxPallets, weightExceeded, palletsExceeded, weightUnknown)
                        + " Fit engine unavailable — arrangement not checked.",
                }
                : baseResult with
                {
                    Verdict = TrailerFitVerdict.Unknown,
                    Rationale = "Trailer-fit verdict unavailable — packing engine unreachable; verify fit at the dock.",
                };
        }

        // Packer ran. Combine its geometry verdict with the capacity arithmetic.
        var packerFails = !summary.ArrangementIsValid || summary.TrailerIsOverweight;
        var verdict = packerFails || capacityExceeded ? TrailerFitVerdict.DoesNotFit : TrailerFitVerdict.Fits;

        var rationale = BuildPackerRationale(summary, verdict, capacityExceeded,
            totalWeight, maxWeight, totalPallets, maxPallets, weightExceeded, palletsExceeded, weightUnknown);

        return baseResult with
        {
            Verdict = verdict,
            Rationale = rationale,
            EstimatedFit = true,
            LinearFeet = summary.LinearFeet,
            CubeUtilization = summary.StackedCubePortionOfTrailer,
        };
    }

    /// <summary>
    /// Expands each load's aggregate signals into standard-pallet shipment lines. Returns the derived
    /// lines plus the total pallet count (null when no load carried a pallet/volume/weight signal).
    /// </summary>
    private (IReadOnlyList<TrailerFitShipmentItem> Items, int? TotalPallets) DeriveShipmentItems(
        IReadOnlyList<TrailerFitItem> items)
    {
        var derived = new List<TrailerFitShipmentItem>();
        var anyPallets = false;
        var totalPallets = 0;

        foreach (var item in items)
        {
            var palletCount = DerivePalletCount(item);
            if (palletCount <= 0) continue;
            anyPallets = true;
            totalPallets += palletCount;

            var height = DeriveHeightInches(item, palletCount);
            // Weight is per-piece; when the load has no weight, use a nominal 1 lb so the packer can
            // still assess geometry — the honest "≥ N lb" flag (WeightUnknown) covers the shortfall.
            var perPieceWeight = item.WeightLbs is > 0
                ? Math.Max(1m, Math.Round(item.WeightLbs.Value / palletCount, 2))
                : 1m;

            derived.Add(new TrailerFitShipmentItem
            {
                Length = PalletLengthInches,
                Width = PalletWidthInches,
                Height = height,
                Weight = perPieceWeight,
                Packing = "PALLET",
                StackLimit = 1,
                NumPieces = palletCount,
                Id = item.LoadRef,
            });
        }

        return (derived, anyPallets ? totalPallets : null);
    }

    private int DerivePalletCount(TrailerFitItem item)
    {
        if (item.Pallets is > 0) return item.Pallets.Value;

        // Volume (assumed cubic feet) ÷ a standard pallet's cube.
        if (item.Volume is > 0)
        {
            var palletCube = PalletFootprintSqInches * _opts.AssumedPalletHeightInches; // cubic inches
            var volumeCubicInches = item.Volume.Value * CubicInchesPerCubicFoot;
            var byVolume = (int)Math.Ceiling(volumeCubicInches / palletCube);
            return Math.Max(1, byVolume);
        }

        // Weight ÷ an assumed per-pallet weight.
        if (item.WeightLbs is > 0 && _opts.AssumedWeightPerPalletLbs > 0)
        {
            var byWeight = (int)Math.Ceiling(item.WeightLbs.Value / _opts.AssumedWeightPerPalletLbs);
            return Math.Max(1, byWeight);
        }

        // No pallet, volume, or weight signal at all — nothing to place for this load.
        return 0;
    }

    private decimal DeriveHeightInches(TrailerFitItem item, int palletCount)
    {
        if (item.Volume is > 0 && palletCount > 0)
        {
            var volumeCubicInches = item.Volume.Value * CubicInchesPerCubicFoot;
            var perPalletVolume = volumeCubicInches / palletCount;
            var height = perPalletVolume / PalletFootprintSqInches;
            if (height > 0) return Math.Round(height, 1);
        }
        return _opts.AssumedPalletHeightInches;
    }

    /// <summary>Deterministic seed from the load refs so the same plan reproduces the same arrangement.</summary>
    private static int DeriveSeed(IReadOnlyList<TrailerFitItem> items)
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in items.OrderBy(i => i.LoadRef, StringComparer.Ordinal))
            {
                foreach (var c in item.LoadRef) hash = hash * 31 + c;
            }
            return hash & 0x7fffffff;
        }
    }

    private static string BuildCapacityRationale(
        decimal totalWeight, decimal maxWeight, int? totalPallets, int maxPallets,
        bool weightExceeded, bool palletsExceeded, bool weightUnknown)
    {
        var parts = new List<string>();
        if (weightExceeded)
        {
            var prefix = weightUnknown ? "≥ " : "";
            parts.Add($"Combined weight {prefix}{totalWeight:N0} lb exceeds trailer max {maxWeight:N0} lb.");
        }
        if (palletsExceeded && totalPallets is int p)
        {
            parts.Add($"Combined {p} pallets exceed trailer capacity {maxPallets}.");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "Within trailer capacity.";
    }

    private static string BuildPackerRationale(
        TrailerFitPlanSummary summary, TrailerFitVerdict verdict, bool capacityExceeded,
        decimal totalWeight, decimal maxWeight, int? totalPallets, int maxPallets,
        bool weightExceeded, bool palletsExceeded, bool weightUnknown)
    {
        if (verdict == TrailerFitVerdict.Fits)
        {
            var lf = summary.LinearFeet is decimal f ? $" ~{f:N1} linear ft" : "";
            return $"Estimated fit — dims assumed from aggregate pallets/weight/volume.{lf} Verify at dock.";
        }

        var parts = new List<string>();
        if (!summary.ArrangementIsValid) parts.Add("Packer could not arrange all pieces in the trailer.");
        if (summary.TrailerIsOverweight) parts.Add("Packer reports the trailer is overweight.");
        if (capacityExceeded)
        {
            parts.Add(BuildCapacityRationale(totalWeight, maxWeight, totalPallets, maxPallets,
                weightExceeded, palletsExceeded, weightUnknown));
        }
        parts.Add("Estimated from assumed dims — verify at dock.");
        return string.Join(" ", parts);
    }
}
