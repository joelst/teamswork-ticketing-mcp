using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the sixth review round.</summary>
public sealed class ReviewRound6Tests
{
    private static readonly TicketUser Actor = new("u1", "Jane Doe", "jane@example.test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_create_answered_with_an_http_200_error_envelope_isnt_retried()
    {
        // The API can report a failure with 200 and error:true after it has already acted; that must count the
        // same as any other post-send failure for a non-idempotent call.
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":null,"error":true,"message":"already resolved"}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.True(ex.OutcomeUnknown);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_read_answered_with_an_http_200_error_envelope_isnt_marked_unknown()
    {
        // Idempotent calls never need the outcome-unknown treatment: repeating them is always safe.
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":null,"error":true,"message":"nope"}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task A_duplicate_page_cant_make_a_partial_scan_look_complete()
    {
        // Pages [1,2] then [1,3] with itemCount 4: four rows are read, but only three distinct tickets are ever
        // seen, so a fourth remains unreached and the scan must be reported as truncated.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":4}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"3"}],"itemCount":4}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, maxTickets: 4, pageSize: 2, Ct);

        Assert.Equal(3, r.Scanned);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task A_complete_scan_with_no_duplicates_isnt_truncated()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":4}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"3"},{"id":"4"}],"itemCount":4}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, maxTickets: 4, pageSize: 2, Ct);

        Assert.Equal(4, r.Scanned);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task A_caller_joining_an_in_flight_read_isnt_charged_or_refused()
    {
        // With a per-caller quota of one request a minute, two concurrent misses from the same caller must not both
        // be charged: the second has to join the first's read rather than spend (and lose) the caller's only permit.
        var handler = new GatedHandler();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1));
        using var quota = new UpstreamQuota(opts);
        var caller = new Caller("tenant/alice");
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, time),
            NullLogger<TicketingClient>.Instance, time, quota, caller);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time, quota, caller);

        Task<Instance> first = Task.Run(() => cache.GetInstanceAsync(client, null, refresh: false, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the first read is out, holding the caller's only permit
        Task<Instance> second = cache.GetInstanceAsync(client, null, refresh: false, Ct); // must join, not charge again
        handler.Release.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, handler.Count);
    }

    private sealed record Caller(string? Key) : IUpstreamCaller;

    /// <summary>Answers every request with the instance; the first is held until released, asynchronously.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private int _count;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, NewToolsTests.InstanceJson);
        }
    }
}
