using System.Net;
using System.Text;
using System.Text.Json;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Covers the real <see cref="HttpTrailerFitService"/>: input derivation from Alvys aggregate
/// signals, resilient degrade when the sidecar is unreachable/slow, and verdict mapping from the
/// packer summary combined with capacity arithmetic. The sidecar HTTP call is faked with a stub
/// <see cref="HttpMessageHandler"/> so no network is touched.
/// </summary>
public sealed class HttpTrailerFitServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static HttpTrailerFitService BuildService(
        StubHandler handler, TrailerFitOptions? opts = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://sidecar.test/") };
        var client = new HttpTrailerFitClient(http);
        var options = Microsoft.Extensions.Options.Options.Create(
            opts ?? new TrailerFitOptions { TimeoutSeconds = 5 });
        return new HttpTrailerFitService(
            client, options, TimeProvider.System, NullLogger<HttpTrailerFitService>.Instance);
    }

    private static StubHandler PackerReturns(TrailerFitPlanSummary summary)
    {
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        return new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    [Fact]
    public void Service_reports_enabled()
    {
        var svc = BuildService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(svc.IsEnabled);
    }

    [Fact]
    public async Task Packer_valid_and_within_capacity_yields_Fits()
    {
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
            LinearFeet = 24.5m,
            StackedCubePortionOfTrailer = 0.6m,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null), new TrailerFitItem("L-2", 10_000m, 6, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Fits, result.Verdict);
        Assert.True(result.EstimatedFit);
        Assert.Equal(24.5m, result.LinearFeet);
        Assert.Equal(0.6m, result.CubeUtilization);
        Assert.Equal(22_000m, result.TotalWeightLbs);
        Assert.Equal(14, result.TotalPallets);
        Assert.False(result.CapacityExceeded);
        Assert.False(result.WeightUnknown);
    }

    [Fact]
    public async Task Packer_reports_invalid_arrangement_yields_DoesNotFit()
    {
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = false,
            TrailerIsOverweight = false,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.DoesNotFit, result.Verdict);
    }

    [Fact]
    public async Task Combined_weight_over_capacity_is_DoesNotFit_even_when_packer_ok()
    {
        // Vertiv-style case: real Alvys weights already blow past the trailer max. Even if the
        // packer's geometry passed, the arithmetic must veto the plan.
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 60_000m, 10, null), new TrailerFitItem("L-2", 24_141m, 10, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.DoesNotFit, result.Verdict);
        Assert.True(result.CapacityExceeded);
        Assert.Equal(84_141m, result.TotalWeightLbs);
    }

    [Fact]
    public async Task Sidecar_failure_degrades_to_Unknown_when_within_capacity()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Unknown, result.Verdict);
        Assert.Contains("verify", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sidecar_failure_still_reports_DoesNotFit_when_capacity_exceeded()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 60_000m, 10, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.DoesNotFit, result.Verdict);
        Assert.True(result.CapacityExceeded);
    }

    [Fact]
    public async Task Non_success_status_degrades_to_Unknown()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public async Task Missing_weight_sets_WeightUnknown_and_floor_total()
    {
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 12_000m, 8, null), new TrailerFitItem("L-2", null, 4, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.True(result.WeightUnknown);
        Assert.Equal(12_000m, result.TotalWeightLbs);   // only the known weight; a floor
        Assert.Null(result.WeightUtilization);          // never computed off an unknown weight
    }

    [Fact]
    public async Task Pallet_count_derived_from_weight_when_no_pallet_signal()
    {
        // No pallet or volume signal: pallet count falls back to weight ÷ assumed-per-pallet.
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
        });
        var opts = new TrailerFitOptions { TimeoutSeconds = 5, AssumedWeightPerPalletLbs = 1_500m };
        var svc = BuildService(handler, opts);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 3_000m, null, null)]);

        var result = await svc.EvaluateAsync(request);

        // 3000 / 1500 = 2 pallets.
        Assert.Equal(2, result.TotalPallets);
    }

    [Fact]
    public async Task No_pallet_volume_or_weight_signal_skips_packer_and_reports_Unknown()
    {
        var handler = new StubHandler(_ =>
        {
            Assert.Fail("Packer must not be called when there is nothing to place.");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", null, null, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Unknown, result.Verdict);
        Assert.Null(result.TotalPallets);
    }

    [Fact]
    public async Task Combined_pallets_over_capacity_is_DoesNotFit()
    {
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [new TrailerFitItem("L-1", 10_000m, 20, null), new TrailerFitItem("L-2", 10_000m, 12, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.DoesNotFit, result.Verdict);
        Assert.True(result.CapacityExceeded);
        Assert.Equal(32, result.TotalPallets);
    }

    // --- Phase 7.1: verdict paths anchored on live-tenant ground-truth weights ---
    // Weights below are the real Alvys va336 Open-load payloads recorded in
    // alvys_ground_truth/open_loads_page1.json (Weight.Value, Pounds):
    //   load 1003516 = 42,500 lb, load 1002398 = 16,800.09 lb, load 1004858 = 43,400 lb.
    // These pin the demo talk track ("we caught a 59,300 lb pair before dispatch") to data.

    [Fact]
    public async Task GroundTruth_pair_over_trailer_max_is_OVER_capacity_even_with_valid_packer()
    {
        // 1003516 (42,500) + 1002398 (16,800.09) = 59,300.09 lb > 45,000 lb standard dry van.
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(null, null, null), // no assigned trailer → standard 45,000 lb spec
            [new TrailerFitItem("1003516", 42_500m, null, null),
             new TrailerFitItem("1002398", 16_800.09m, null, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.DoesNotFit, result.Verdict); // maps to the OVER chip in the SPA
        Assert.True(result.CapacityExceeded);
        Assert.False(result.WeightUnknown);
        Assert.Equal(59_300.09m, result.TotalWeightLbs);
        Assert.Equal(45_000m, result.TrailerMaxWeightLbs);
    }

    [Fact]
    public async Task GroundTruth_single_load_within_capacity_is_PASS()
    {
        // 1002398 alone (16,800.09 lb) sits well under the 45,000 lb max → packer-backed PASS.
        var handler = PackerReturns(new TrailerFitPlanSummary
        {
            ArrangementIsValid = true,
            TrailerIsOverweight = false,
            LinearFeet = 18.0m,
            StackedCubePortionOfTrailer = 0.42m,
        });
        var svc = BuildService(handler);

        var request = new TrailerFitRequest(
            new TrailerCapacitySpec(null, null, null),
            [new TrailerFitItem("1002398", 16_800.09m, null, null)]);

        var result = await svc.EvaluateAsync(request);

        Assert.Equal(TrailerFitVerdict.Fits, result.Verdict); // maps to the PASS chip in the SPA
        Assert.False(result.CapacityExceeded);
        Assert.True(result.EstimatedFit);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
