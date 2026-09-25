using System.Net;
using System.Text.Json;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class TicketingClientTests
{
    private static readonly Guid TicketId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");
    private static readonly TicketUser Actor = new("00000000-0000-0000-0000-000000000001", "Test Agent", "agent@example.test");

    [Fact]
    public async Task ListTickets_sends_key_timezone_filters_and_continuation_header()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[{"id":"abc","title":"T"}],"itemCount":1,"continuationToken":"next"}""");
        TicketingClient client = TestFactory.Client(handler);

        ListResponse<Ticket> r = await client.ListTicketsAsync(new TicketListQuery
        {
            Search = "printer",
            Priority = "Urgent",
            IsResolved = false,
            Limit = 5,
            Offset = 10,
            Select = "id,title",
            ContinuationToken = "tok-1",
            IncludeHtml = true,
        }, TestContext.Current.CancellationToken);

        CapturedRequest req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/ticketing/v1/tickets", req.Uri.AbsolutePath);

        Dictionary<string, string> q = TestFactory.Query(req.Uri);
        Assert.Equal(TestFactory.ApiKey, q["key"]);
        Assert.Equal("-6", q["timezone"]); // America/Chicago in January (CST)
        Assert.Equal("printer", q["search"]);
        Assert.Equal("Urgent", q["priority"]);
        Assert.Equal("false", q["isResolved"]);
        Assert.Equal("5", q["limit"]);
        Assert.Equal("10", q["offset"]);
        Assert.Equal("id,title", q["select"]);
        Assert.Equal("description_HTML", q["include"]);
        Assert.False(q.ContainsKey("status"));

        Assert.True(req.Headers.TryGetValues("continuationToken", out IEnumerable<string>? values));
        Assert.Equal("tok-1", Assert.Single(values!));

        Assert.Equal("next", r.ContinuationToken);
        Assert.Equal(1, r.ItemCount);
        Assert.Equal("T", Assert.Single(r.Items!).Title);
    }

    [Fact]
    public async Task Explicit_timezone_offset_overrides_default()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":{"id":"x"}}""");
        TicketingClient client = TestFactory.Client(handler);

        await client.GetInstanceAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal("7", TestFactory.Query(handler.Requests[0].Uri)["timezone"]);
    }

    [Fact]
    public async Task Daylight_saving_changes_default_offset()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        TicketingClient client = TestFactory.Client(handler, time: new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));

        await client.ListTicketTagsOrTicketsAsync();

        Assert.Equal("-5", TestFactory.Query(handler.Requests[0].Uri)["timezone"]); // CDT
    }

    [Fact]
    public async Task Tags_endpoint_has_no_timezone()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[{"id":"c1","text":"Area"}]}""");
        TicketingClient client = TestFactory.Client(handler);

        ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(TestContext.Current.CancellationToken);

        Dictionary<string, string> q = TestFactory.Query(handler.Requests[0].Uri);
        Assert.False(q.ContainsKey("timezone"));
        Assert.Equal("/ticketing/v1/tags", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("Area", r.Items![0].Text);
    }

    [Fact]
    public async Task CreateTicket_posts_expected_body_and_omits_nulls()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.Created, """{"item":{"id":"new","ticketNo":42,"title":"Printer down"},"error":false,"message":"ok"}""");
        TicketingClient client = TestFactory.Client(handler);

        var write = new TicketWrite { Title = "Printer down", Requestor = Actor, Priority = "Urgent", ExpectedDate = "2026-04-01" };
        Ticket created = await client.CreateTicketAsync(write, Actor, includeHtml: false, timezoneOffset: null, TestContext.Current.CancellationToken);

        Assert.Equal(42, created.TicketNo);
        CapturedRequest req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);

        using JsonDocument body = JsonDocument.Parse(req.Body!);
        JsonElement ticket = body.RootElement.GetProperty("ticket");
        Assert.Equal("Printer down", ticket.GetProperty("title").GetString());
        Assert.Equal("Urgent", ticket.GetProperty("priority").GetString());
        Assert.Equal("2026-04-01", ticket.GetProperty("expectedDate").GetString());
        Assert.False(ticket.TryGetProperty("description", out _));
        Assert.False(ticket.TryGetProperty("assignee", out _));
        Assert.Equal(Actor.Email, body.RootElement.GetProperty("user").GetProperty("email").GetString());
        Assert.DoesNotContain(TestFactory.ApiKey, req.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateStatus_uses_status_route()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":{"id":"x","status":"Resolved"}}""");
        TicketingClient client = TestFactory.Client(handler);

        Ticket t = await client.UpdateTicketStatusAsync(TicketId, "Resolved", "fixed", "done", Actor, null, TestContext.Current.CancellationToken);

        Assert.Equal("Resolved", t.Status);
        Assert.Equal($"/ticketing/v1/tickets/{TicketId}/status", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("\"resolution\":\"fixed\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_html_property_name_matches_api()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.Created, """{"item":{"activityId":"a1","ticketId":"t","comment":"","attachments":[],"user":{"id":"1","name":"n","email":"e@x"}}}""");
        TicketingClient client = TestFactory.Client(handler);

        await client.AddCommentAsync(TicketId, null, "<p>hi</p>", isPrivate: true, Actor, includeHtml: true, TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("<p>hi</p>", body.RootElement.GetProperty("comment_HTML").GetString());
        Assert.False(body.RootElement.TryGetProperty("comment", out _));
        Assert.True(body.RootElement.GetProperty("isPrivate").GetBoolean());
        Assert.Equal("comment_HTML", TestFactory.Query(handler.Requests[0].Uri)["include"]);
    }

    [Fact]
    public async Task Unauthorized_maps_to_clear_message_without_leaking_key()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.Unauthorized, """{"error":true,"message":"Invalid API key"}""");
        TicketingClient client = TestFactory.Client(handler);

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => client.GetInstanceAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestFactory.ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Error_flag_in_200_body_is_an_error()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[],"error":true,"message":"Instance disabled"}""");
        TicketingClient client = TestFactory.Client(handler);

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => client.ListTicketsAsync(new TicketListQuery(), TestContext.Current.CancellationToken));

        Assert.Contains("Instance disabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retries_on_429_then_succeeds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""")
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i"}}""");
        TicketingClient client = TestFactory.Client(handler);

        Instance i = await client.GetInstanceAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("i", i.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Post_is_not_retried_on_gateway_errors_to_avoid_duplicates()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.BadGateway, """{"message":"upstream"}""")
            .Enqueue(HttpStatusCode.Created, """{"item":{"activityId":"dup"}}""");
        TicketingClient client = TestFactory.Client(handler);

        await Assert.ThrowsAsync<TicketingApiException>(() =>
            client.AddCommentAsync(TicketId, "hi", null, false, Actor, false, TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Post_is_retried_on_429_because_it_was_not_processed()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""")
            .Enqueue(HttpStatusCode.Created, """{"item":{"activityId":"a1"}}""");
        TicketingClient client = TestFactory.Client(handler);

        CommentActivity c = await client.AddCommentAsync(TicketId, "hi", null, false, Actor, false, TestContext.Current.CancellationToken);

        Assert.Equal("a1", c.ActivityId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Put_is_retried_on_gateway_errors()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"message":"busy"}""")
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"x","status":"Closed"}}""");
        TicketingClient client = TestFactory.Client(handler);

        Ticket t = await client.UpdateTicketStatusAsync(TicketId, "Closed", null, null, Actor, null, TestContext.Current.CancellationToken);

        Assert.Equal("Closed", t.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_consume_rate_limit_permits()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i"}}""");
        // Two permits per window: the first call and its retry use both, so a second call must be refused.
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => { o.RateLimitPermits = 2; o.RateLimitWindowSeconds = 3600; }));

        await client.GetInstanceAsync(null, TestContext.Current.CancellationToken);

        // The limiter queues rather than rejects, so the third call waits for a permit; bound the wait.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInstanceAsync(null, cts.Token));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_on_400()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"title required"}""");
        TicketingClient client = TestFactory.Client(handler);

        await Assert.ThrowsAsync<TicketingApiException>(() => client.CreateTicketAsync(new TicketWrite(), Actor, false, null, TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unknown_ticket_fields_are_preserved()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":{"id":"x","futureField":{"a":1}}}""");
        TicketingClient client = TestFactory.Client(handler);

        Ticket t = await client.GetTicketAsync(TicketId, false, null, TestContext.Current.CancellationToken);

        Assert.NotNull(t.Extra);
        Assert.True(t.Extra!.ContainsKey("futureField"));
    }

    [Fact]
    public async Task Missing_api_key_fails_fast_without_calling_upstream()
    {
        var handler = new FakeHttpHandler();
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => o.ApiKey = null));

        await Assert.ThrowsAsync<TicketingApiException>(() => client.GetInstanceAsync(null, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }
}

internal static class TicketingClientTestExtensions
{
    public static Task ListTicketTagsOrTicketsAsync(this TicketingClient client) =>
        client.ListTicketsAsync(new TicketListQuery(), TestContext.Current.CancellationToken);
}
