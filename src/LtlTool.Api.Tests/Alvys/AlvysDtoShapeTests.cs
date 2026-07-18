using System.Text.Json;
using LtlTool.Api.Features.Integrations.Alvys;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Round-trip tests that pin the empirical wire shapes captured 2026-07-18 via live MCP
/// against the va336 production tenant. These fail loudly if a future change reintroduces
/// the older-and-incorrect <c>decimal?</c> shape on the Trip mileage / value fields, or
/// drops one of the seven <see cref="AlvysReference"/> fields.
///
/// <para>
/// Source of the fixture payloads: real responses from <c>trips_search</c>,
/// <c>loads_get_by_id</c>, and <c>customers_get_by_id</c> on the va336 tenant. Only
/// non-identifying fields are asserted \u2014 no load numbers, no customer names.
/// </para>
/// </summary>
public sealed class AlvysDtoShapeTests
{
    [Fact]
    public void AlvysTrip_deserializes_LoadedMileage_as_distance_measurement()
    {
        // Verbatim shape from a live trips_search response.
        const string json = """
            {
              "Id": "008db10507674188b07d4d3614bc5efb",
              "TripNumber": "1003703",
              "Status": "In Transit",
              "TotalMileage":  { "Distance": { "Value": 2220.0, "UnitOfMeasure": "Miles" }, "Source": "Engine",  "ProfileName": "PCMiler" },
              "EmptyMileage":  { "Distance": { "Value": 0.0,    "UnitOfMeasure": "Miles" }, "Source": "Engine",  "ProfileName": "PCMiler" },
              "LoadedMileage": { "Distance": { "Value": 2220.0, "UnitOfMeasure": "Miles" }, "Source": "Engine",  "ProfileName": "PCMiler" },
              "TripValue":     { "Amount": 12400.0, "Currency": 840 },
              "References":    []
            }
            """;

        var trip = JsonSerializer.Deserialize<AlvysTrip>(json);

        Assert.NotNull(trip);
        Assert.Equal(2220m, trip!.TotalMileage?.Value);
        Assert.Equal(0m, trip.EmptyMileage?.Value);
        Assert.Equal(2220m, trip.LoadedMileage?.Value);
        Assert.Equal("Miles", trip.LoadedMileage?.Distance?.UnitOfMeasure);
        Assert.Equal("Engine", trip.LoadedMileage?.Source);
        Assert.Equal("PCMiler", trip.LoadedMileage?.ProfileName);
        Assert.Equal(12400m, trip.TripValue?.Amount);
        // AlvysMoneyCurrencyConverter translates the numeric ISO-4217 code (840) to alpha.
        // This normalises trips-endpoint payloads to look like invoices-endpoint payloads.
        Assert.Equal("USD", trip.TripValue?.Currency);
    }

    [Fact]
    public void AlvysMoney_Currency_reads_both_numeric_and_string_wire_shapes()
    {
        // Trip endpoint: numeric 840.
        var trip = JsonSerializer.Deserialize<AlvysMoney>("""{"Amount":100,"Currency":840}""");
        Assert.Equal("USD", trip?.Currency);

        // Invoice endpoint: alpha string.
        var invoice = JsonSerializer.Deserialize<AlvysMoney>("""{"Amount":100,"Currency":"USD"}""");
        Assert.Equal("USD", invoice?.Currency);

        // Unknown numeric code falls through as string form so nothing silently drops.
        var unknown = JsonSerializer.Deserialize<AlvysMoney>("""{"Amount":100,"Currency":999}""");
        Assert.Equal("999", unknown?.Currency);

        // Null currency preserved as null.
        var noCurrency = JsonSerializer.Deserialize<AlvysMoney>("""{"Amount":100}""");
        Assert.Null(noCurrency?.Currency);
    }

    [Fact]
    public void AlvysTrip_deserializes_missing_mileage_fields_as_null_not_zero()
    {
        // Empirical: quoted/queued trips can omit LoadedMileage entirely. Should remain
        // null instead of collapsing to 0 miles \u2014 zero would masquerade as "we know it's zero"
        // which is exactly the anti-failure map 3o (silent misses) trap.
        const string json = """
            { "Id": "abc", "TripNumber": "1", "Status": "Quoted" }
            """;

        var trip = JsonSerializer.Deserialize<AlvysTrip>(json);

        Assert.NotNull(trip);
        Assert.Null(trip!.LoadedMileage);
        Assert.Null(trip.TotalMileage);
        Assert.Null(trip.TripValue);
    }

    [Fact]
    public void Driver_RPM_from_TripValue_and_LoadedMileage_matches_yard_visit_math()
    {
        // Grounds decision #12 (driver RPM = TripValue.Amount / LoadedMileage.Distance.Value)
        // to a concrete, verifiable formula. Uses the exact values from the empirical
        // trips_search sample so the number below (5.585\u2026) is not an invented target.
        var trip = new AlvysTrip
        {
            Id = "T",
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = 2220m, UnitOfMeasure = "Miles" },
            },
            TripValue = new AlvysMoney { Amount = 12400m, Currency = "USD" },
        };

        var driverRpm =
            trip.TripValue!.Amount!.Value / trip.LoadedMileage!.Value!.Value;

        Assert.Equal(5.585585m, Math.Round(driverRpm, 6));
    }

    [Fact]
    public void AlvysReference_deserializes_all_seven_empirical_fields()
    {
        // Verbatim reference row from a live loads_get_by_id response.
        const string json = """
            {
              "Id": "9f24c3-...-01",
              "ReferenceId": null,
              "Name": "Method of Payment",
              "Value": "Prepaid (by Seller)",
              "Type": "Text",
              "Access": "Public",
              "Origin": "EDI"
            }
            """;

        var reference = JsonSerializer.Deserialize<AlvysReference>(json);

        Assert.NotNull(reference);
        Assert.Equal("9f24c3-...-01", reference!.Id);
        Assert.Null(reference.ReferenceId);
        Assert.Equal("Method of Payment", reference.Name);
        Assert.Equal("Prepaid (by Seller)", reference.Value);
        Assert.Equal("Text", reference.Type);
        Assert.Equal("Public", reference.Access);
        Assert.Equal("EDI", reference.Origin);
    }

    [Fact]
    public void AlvysReference_LTL_boolean_serializes_as_string()
    {
        // Reuben transcript 21:25: "Parameter ID string" \u2014 references are stringly-typed on
        // the wire, even for the LTL boolean. When Phase 5 constructs a payload to write
        // the LTL reference on a parent trip, the value must be a "true"/"false" string,
        // not a JSON boolean.
        var reference = new AlvysReference
        {
            Name = "LTL",
            Value = "true",
            Type = "Text",
        };

        var wire = JsonSerializer.Serialize(reference);

        Assert.Contains("\"Value\":\"true\"", wire);
        Assert.DoesNotContain("\"Value\":true", wire);
    }
}
