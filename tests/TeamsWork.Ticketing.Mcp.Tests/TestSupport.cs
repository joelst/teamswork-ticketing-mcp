using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Records outbound requests and replays scripted responses.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public FakeHttpHandler Enqueue(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => Json(status, json));
        return this;
    }

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left for " + request.RequestUri!.AbsolutePath);
        }

        return _responses.Dequeue()(request);
    }
}

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, HttpRequestHeaders Headers);

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal static class TestFactory
{
    public const string ApiKey = "unit-test-key-000";

    public static TicketingOptions Options(Action<TicketingOptions>? configure = null)
    {
        var o = new TicketingOptions { ApiKey = ApiKey, BaseUrl = "https://ticketing.example.test/ticketing/v1" };
        configure?.Invoke(o);
        return o;
    }

    public static TicketingClient Client(FakeHttpHandler handler, TicketingOptions? options = null, TimeProvider? time = null)
    {
        options ??= Options();
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(options);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var tz = new TimeZoneOffsetResolver(opts, time ?? new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        return new TicketingClient(http, opts, new TicketingRateLimiter(opts), tz, NullLogger<TicketingClient>.Instance, time);
    }

    public static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.Ordinal);
}
