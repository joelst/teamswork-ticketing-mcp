using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TeamsWork.Ticketing.Mcp.Observability;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class ServiceMetricsTests
{
    [Fact]
    public void Records_low_cardinality_http_and_upstream_metrics()
    {
        var measurements = new ConcurrentQueue<(string Name, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ServiceMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, string.Join(",", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, string.Join(",", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")))));
        listener.Start();

        ServiceMetrics.RecordHttpRequest("POST", "mcp", 200, errorType: null, durationMs: 12);
        ServiceMetrics.RecordHttpRequest("X-Unknown-Method", "mcp", 200, errorType: null, durationMs: 13);
        ServiceMetrics.RecordUpstreamRequest(HttpMethod.Get, "tickets.activities", 429, errorType: null, durationMs: 42);
        ServiceMetrics.RecordUpstreamRetry(HttpMethod.Get, "tickets.activities");
        ServiceMetrics.RecordUpstreamResponseBytes("tickets.activities", 1024);
        ServiceMetrics.RecordUpstreamFailure("tickets.activities", 200, "invalid_response");

        (string Name, string Tags)[] snapshot = measurements.ToArray();
        Assert.Contains(snapshot, item => item.Name == "ticketing.mcp.http.requests" && item.Tags.Contains("http.route=mcp", StringComparison.Ordinal));
        Assert.Contains(snapshot, item => item.Name == "ticketing.upstream.requests" && item.Tags.Contains("http.response.status_code=429", StringComparison.Ordinal));
        Assert.Contains(snapshot, item => item.Name == "ticketing.mcp.http.requests" && item.Tags.Contains("http.request.method=_OTHER", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, item => item.Tags.Contains("X-Unknown-Method", StringComparison.Ordinal));
        Assert.Contains(snapshot, item => item.Name == "ticketing.upstream.retries" && item.Tags.Contains("ticketing.operation=tickets.activities", StringComparison.Ordinal));
        Assert.Contains(snapshot, item => item.Name == "ticketing.upstream.response.size");
        Assert.Contains(snapshot, item => item.Name == "ticketing.upstream.failures" &&
            item.Tags.Contains("error.type=invalid_response", StringComparison.Ordinal) &&
            item.Tags.Contains("http.response.status_code=200", StringComparison.Ordinal));
        Assert.All(snapshot, item => Assert.DoesNotContain("ticketId", item.Tags, StringComparison.OrdinalIgnoreCase));
    }
}
