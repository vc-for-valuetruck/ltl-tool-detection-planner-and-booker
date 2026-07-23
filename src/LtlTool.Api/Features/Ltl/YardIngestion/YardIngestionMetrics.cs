using System.Diagnostics.Metrics;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// Counters for the ingestion pipeline, published on the <c>LtlTool.YardIngestion</c> meter so an
/// OpenTelemetry/Prometheus exporter can scrape accept/duplicate/reject rates without parsing logs.
/// Registered as a singleton; the underlying instruments are thread-safe.
/// </summary>
public sealed class YardIngestionMetrics : IDisposable
{
    public const string MeterName = "LtlTool.YardIngestion";

    private readonly Meter _meter;
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _duplicate;
    private readonly Counter<long> _rejected;

    public YardIngestionMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _accepted = _meter.CreateCounter<long>("yard_events.accepted", description: "Newly accepted Yard events.");
        _duplicate = _meter.CreateCounter<long>("yard_events.duplicate", description: "Duplicate Yard events acked without reprocessing.");
        _rejected = _meter.CreateCounter<long>("yard_events.rejected", description: "Yard events rejected by validation.");
    }

    public void Accepted(YardEventCategory category) =>
        _accepted.Add(1, new KeyValuePair<string, object?>("category", category.ToString()));

    public void Duplicate(YardEventCategory category) =>
        _duplicate.Add(1, new KeyValuePair<string, object?>("category", category.ToString()));

    public void Rejected() => _rejected.Add(1);

    public void Dispose() => _meter.Dispose();
}
