using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Auth;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the sixth review round.</summary>
public sealed class ReviewRound6Tests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string Outsider = "22222222-2222-2222-2222-222222222222";
    private static readonly TicketUser Actor = new("u1", "Jane Doe", "jane@example.test");

    private static readonly Instance Listed = JsonSerializer.Deserialize<ItemResponse<Instance>>(
        """{"item":{"id":"i","assignees":{"peoples":[{"id":"11111111-1111-1111-1111-111111111111","name":"Jane Doe","email":"jane.doe@contoso.com"}]}}}""",
        TicketingClient.JsonOptions)!.Item!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- HTTP-200 error envelopes ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_error_envelope_after_a_create_is_an_unknown_outcome()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":null,"error":true,"message":"Something failed"}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.True(ex.OutcomeUnknown);
        Assert.Contains("Something failed", ex.Message, StringComparison.Ordinal); // the API's own message is kept
    }

    [Fact]
    public async Task An_error_envelope_on_a_read_is_a_plain_error()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[],"error":true,"message":"Instance disabled"}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.False(ex.OutcomeUnknown);
    }

    // ---- Which failures prove a request wasn't sent ------------------------------------------------------------------------

    [Fact]
    public async Task A_create_whose_connection_is_refused_is_retried()
    {
        // A refused connect happens before any byte of the request is sent, so repeating the create is safe.
        var handler = new FakeHttpHandler()
            .Enqueue(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused)))
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");

        Ticket created = await TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct);

        Assert.Equal(TicketA, created.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Only_a_connect_failure_counts_as_a_connection_error_before_sending()
    {
        Assert.True(TicketingClient.IsPreSendFailure(new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.HostUnreachable))));
        Assert.False(TicketingClient.IsPreSendFailure(new HttpRequestException(HttpRequestError.ConnectionError, "x", new IOException("reset"))));
        Assert.False(TicketingClient.IsPreSendFailure(new HttpRequestException(HttpRequestError.ConnectionError, "x")));
        Assert.False(TicketingClient.IsPreSendFailure(new HttpRequestException(HttpRequestError.ResponseEnded, "x")));
    }

    [Fact]
    public void The_priority_follow_up_outlasts_the_clients_own_retries()
    {
        TicketingOptions o = TestFactory.Options();
        // Three attempts, each able to wait a rate-limit window and then time out, and two capped Retry-After delays.
        Assert.True(TicketingClient.LongestRequest(o) >= TimeSpan.FromSeconds(3 * (o.RateLimitWindowSeconds + o.RequestTimeoutSeconds) + 20));
    }

    // ---- Scan completeness is proven by distinct tickets -----------------------------------------------------------------

    [Fact]
    public async Task A_page_with_a_repeat_and_a_new_ticket_cant_make_a_scan_look_complete()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":4}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"3"}],"itemCount":4}""") // four rows read, three tickets
            .Enqueue(HttpStatusCode.OK, """{"items":[],"itemCount":4}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.Equal(3, r.Scanned);
        Assert.True(r.Truncated);
        Assert.Equal(3, handler.Requests.Count); // it looked for the fourth ticket rather than stopping at four rows
    }

    [Fact]
    public async Task Rows_without_an_id_count_towards_completeness_while_nothing_repeats()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":null}],"itemCount":2}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 5, Ct);

        Assert.False(r.Truncated);
        Assert.Equal(1, r.Scanned);
    }

    [Theory]
    [InlineData("""{"items":[],"continuationToken":"next"}""", true)]  // a token says more may exist
    [InlineData("""{"items":[],"itemCount":3}""", true)]               // a total not reached
    [InlineData("""{"items":[]}""", false)]                            // nothing says there's more: an empty instance
    [InlineData("""{"items":[],"itemCount":0}""", false)]
    public async Task An_empty_page_is_the_end_only_if_nothing_says_otherwise(string page, bool truncated)
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, page);

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 10, Ct);

        Assert.Equal(truncated, r.Truncated);
        Assert.Single(handler.Requests); // and it stops there, rather than paging on from no progress
    }

    [Fact]
    public async Task An_empty_page_with_a_token_after_real_pages_is_incomplete()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"continuationToken":"a"}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[],"continuationToken":"b"}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.Equal(2, r.Scanned);
        Assert.True(r.Truncated);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- Starting a shared read is atomic -------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_joins_a_read_being_started_instead_of_being_charged_for_another()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1));
        var handler = new HeldHandler(InstanceJson);
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var quota = new UpstreamQuota(opts);
        var caller = new FixedCaller("tenant/alice");
        TicketingClient client = ClientFor(handler);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time, quota, caller);

        // Alice starts a read (her one request this minute); while it is out, she asks again. The second call must join
        // it: charging her again would refuse it, and starting a second read would waste one.
        Task<Instance> first = Task.Run(() => cache.GetInstanceAsync(client, null, refresh: false, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<Instance> second = cache.GetInstanceAsync(client, null, refresh: false, Ct);
        handler.Release.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task A_refresh_takes_the_whole_caches_slot_as_soon_as_it_starts()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        var handler = new HeldHandler(InstanceJson, hold: 2);
        var time = new MutableTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        TicketingClient client = ClientFor(handler);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time);

        await cache.GetInstanceAsync(client, -5, refresh: false, Ct); // a copy for -5
        time.Advance(TimeSpan.FromMinutes(1));                           // old enough that a refresh would re-read it

        Task<Instance> refreshing = Task.Run(() => cache.GetInstanceAsync(client, -6, refresh: true, Ct), Ct); // held
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct); // another offset while it is out: an ordinary read
        Assert.Equal(2, handler.Count);

        handler.Release.SetResult();
        await refreshing;
    }

    // ---- Names and emails of people outside the assignee list -------------------------------------------------------------

    [Theory]
    [InlineData("\U0001D409\U0001D41A\U0001D427\U0001D41E \U0001D403\U0001D428\U0001D41E")] // mathematical bold
    [InlineData("\u24BFane Doe")]                   // circled capital J
    [InlineData("\u1D0A\u1D00\u0274\u1D07 \u1D05\u1D0F\u1D07")] // small capitals
    [InlineData("J\u0430ne D\u043Ee")]             // Cyrillic a and o
    [InlineData("Jane\u2800Doe")]                   // the blank braille pattern as a space
    [InlineData("Ja\u0301ne Doe")]                  // a combining accent
    public void Styled_and_look_alike_letters_cant_borrow_a_listed_name(string name)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef(Outsider, name, "pat@outside.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("uses the name of Jane Doe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_width_joiners_are_allowed_in_names()
    {
        TicketUser user = InstanceLookup.ResolvePerson(new UserRef(Outsider, "Mehr\u200Cdad Rahimi", "mehrdad@outside.test"), "requestor", Listed, assigneeOnly: false);

        Assert.Equal("Mehr\u200Cdad Rahimi", user.Name);
    }

    [Fact]
    public void An_outsiders_email_cant_use_look_alike_letters()
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef(Outsider, "Pat", "j\u0430ne.doe@contoso.com"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("all ASCII", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_email_to_ticket_form_is_sent_with_one_canonical_address()
    {
        TicketUser user = InstanceLookup.ResolvePerson(new UserRef("Pat@OUTSIDE.test", "Pat", "Pat@OUTSIDE.test"), "requestor", Listed, assigneeOnly: false);

        Assert.Equal("Pat@outside.test", user.Id);
        Assert.Equal("Pat@outside.test", user.Email);
    }

    [Theory]
    [InlineData("contoso..com")]
    [InlineData("-contoso.com")]
    [InlineData("contoso-.com")]
    [InlineData("10.0.0.1")]
    [InlineData("[10.0.0.1]")]
    [InlineData("localhost")]
    public void A_domain_must_be_a_host_name(string domain)
    {
        Assert.False(ToolValidation.IsHostName(domain));
        Assert.NotEmpty(TestFactory.Options(o => o.ExternalEmailDomains = domain).InvalidExternalEmailDomains());
    }

    [Fact]
    public void An_email_at_an_ip_literal_is_refused()
    {
        Assert.Contains("domain name", Assert.Throws<McpException>(() => ToolValidation.RequireEmail("pat@[10.0.0.1]", "email")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_account_is_used_in_canonical_form()
    {
        TicketingOptions options = TestFactory.Options(o => o.ServiceAccount = new ServiceAccountOptions
        {
            Id = " svc@CONTOSO.com ",
            Name = " Help desk bot ",
            Email = "Svc@CONTOSO.com",
        });

        ActingUser user = await new ServiceAccountActingUserProvider(Microsoft.Extensions.Options.Options.Create(options)).GetActingUserAsync(Ct);

        Assert.Equal(("Svc@contoso.com", "Help desk bot", "Svc@contoso.com"), (user.Id, user.Name, user.Email));
    }

    // ---- The invariant-globalisation CI leg -------------------------------------------------------------------------------

    [Fact]
    public void The_invariant_test_run_really_runs_without_culture_data()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXPECT_INVARIANT_GLOBALIZATION") == "1", "Only the invariant CI leg checks this.");

        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant);
        Assert.Throws<CultureNotFoundException>(() => new CultureInfo("fr-FR"));
    }

    private sealed record FixedCaller(string? Key) : IUpstreamCaller;
}
