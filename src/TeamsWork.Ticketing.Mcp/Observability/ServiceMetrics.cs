using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TeamsWork.Ticketing.Mcp.Observability;

internal static class ServiceMetrics
{
    public const string MeterName = "TeamsWork.Ticketing.Mcp";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("ticketing.mcp.http.requests");
    private static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("ticketing.mcp.http.duration", "ms");
    private static readonly Counter<long> UpstreamRequests = Meter.CreateCounter<long>("ticketing.upstream.requests");
    private static readonly Histogram<double> UpstreamDuration = Meter.CreateHistogram<double>("ticketing.upstream.duration", "ms");
    private static readonly Counter<long> UpstreamRetries = Meter.CreateCounter<long>("ticketing.upstream.retries");
    private static readonly Histogram<long> UpstreamResponseBytes = Meter.CreateHistogram<long>("ticketing.upstream.response.size", "By");

    public static void RecordHttpRequest(string method, string route, int? statusCode, string? errorType, double durationMs)
    {
        var tags = new TagList
        {
            { "http.request.method", MethodTag(method) },
            { "http.route", route },
        };
        if (statusCode is not null)
        {
            tags.Add("http.response.status_code", statusCode.Value);
        }
        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        HttpRequests.Add(1, tags);
        HttpDuration.Record(durationMs, tags);
    }

    public static void RecordUpstreamRequest(HttpMethod method, string operation, int? statusCode, string? errorType, double durationMs)
    {
        var tags = new TagList
        {
            { "http.request.method", MethodTag(method.Method) },
            { "ticketing.operation", operation },
        };
        if (statusCode is not null)
        {
            tags.Add("http.response.status_code", statusCode.Value);
        }
        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        UpstreamRequests.Add(1, tags);
        UpstreamDuration.Record(durationMs, tags);
    }

    public static void RecordUpstreamRetry(HttpMethod method, string operation)
    {
        var tags = new TagList
        {
            { "http.request.method", MethodTag(method.Method) },
            { "ticketing.operation", operation },
        };
        UpstreamRetries.Add(1, tags);
    }

    public static void RecordUpstreamResponseBytes(string operation, long bytes) =>
        UpstreamResponseBytes.Record(bytes, new TagList { { "ticketing.operation", operation } });

    public static string RouteFor(PathString path) =>
        path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ? "mcp" :
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) ? "health" :
        "other";

    private static string MethodTag(string method) => method switch
    {
        "CONNECT" or "DELETE" or "GET" or "HEAD" or "OPTIONS" or "PATCH" or "POST" or "PUT" or "TRACE" => method,
        _ => "_OTHER",
    };

    public static double ElapsedMilliseconds(TimeProvider timeProvider, long started) =>
        timeProvider.GetElapsedTime(started).TotalMilliseconds;
}
