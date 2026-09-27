using System.Net;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>
/// How date filters reach the API. Checked on the live API: a filter is honoured only as a plain date, and the
/// 'timezone' parameter shifts its day boundaries the opposite way to the spec (day D starts at D 00:00 UTC plus that
/// many hours), so a request with a date filter sends the caller's offset negated.
/// </summary>
public sealed class DateFilterTests
{
    private const string Empty = """{"items":[]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-5, "5")]   // US Central daylight time: day D starts at D 05:00 UTC
    [InlineData(10, "-10")] // Sydney standard time: day D starts at D-1 14:00 UTC
    [InlineData(0, "0")]
    [InlineData(14, "-14")] // the far ends of the range negate to values the API accepts
    [InlineData(-12, "12")]
    public async Task A_date_filtered_list_sends_the_offset_negated(int offset, string sent)
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);

        await TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { LastUpdateAfter = "2026-09-25", TimezoneOffset = offset }, Ct);

        Dictionary<string, string> query = TestFactory.Query(handler.Requests[0].Uri);
        Assert.Equal(sent, query["timezone"]);
        Assert.Equal("2026-09-25", query["lastUpdateAfter"]);
    }

    [Theory]
    [InlineData(nameof(TicketListQuery.CreatedAfter))]
    [InlineData(nameof(TicketListQuery.CreatedBefore))]
    [InlineData(nameof(TicketListQuery.ExpectedDateAfter))]
    [InlineData(nameof(TicketListQuery.ExpectedDateBefore))]
    [InlineData(nameof(TicketListQuery.LastUpdateAfter))]
    [InlineData(nameof(TicketListQuery.LastUpdateBefore))]
    public async Task Every_date_filter_negates_the_offset(string filter)
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);
        TicketListQuery q = filter switch
        {
            nameof(TicketListQuery.CreatedAfter) => new() { CreatedAfter = "2026-01-01" },
            nameof(TicketListQuery.CreatedBefore) => new() { CreatedBefore = "2026-01-01" },
            nameof(TicketListQuery.ExpectedDateAfter) => new() { ExpectedDateAfter = "2026-01-01" },
            nameof(TicketListQuery.ExpectedDateBefore) => new() { ExpectedDateBefore = "2026-01-01" },
            nameof(TicketListQuery.LastUpdateAfter) => new() { LastUpdateAfter = "2026-01-01" },
            _ => new() { LastUpdateBefore = "2026-01-01" },
        };

        await TestFactory.Client(handler).ListTicketsAsync(q with { TimezoneOffset = -6 }, Ct);

        Assert.Equal("6", TestFactory.Query(handler.Requests[0].Uri)["timezone"]);
    }

    // An expected date is a calendar date, stored as its midnight UTC, and the API compares "after" as at-or-after and
    // "before" as at-or-before the boundary D 00:00 UTC + timezone. These are the live API's answers, for a ticket due
    // 2026-10-15, at each timezone value sent, and the dates the server must send to get them right.
    [Theory]
    [InlineData(-14, "2026-10-15", "2026-10-15")]
    [InlineData(-5, "2026-10-15", "2026-10-15")]
    [InlineData(0, "2026-10-15", "2026-10-14")]
    [InlineData(5, "2026-10-14", "2026-10-14")]
    [InlineData(12, "2026-10-14", "2026-10-14")]
    public void An_expected_date_filter_matches_calendar_dates_whatever_the_offset(int sent, string afterSent, string beforeSent)
    {
        Assert.Equal(afterSent, TicketingClient.ExpectedDateBound("2026-10-15", sent, after: true));
        Assert.Equal(beforeSent, TicketingClient.ExpectedDateBound("2026-10-15", sent, after: false));

        // So a ticket due on 2026-10-15 (stored as 2026-10-15T00:00Z) is "on or after the 15th" and not "before the 15th":
        DateTimeOffset due = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.True(due >= Boundary(afterSent, sent));
        Assert.False(due <= Boundary(beforeSent, sent));
        // and "before the 16th" but not "on or after the 16th".
        Assert.True(due <= Boundary(TicketingClient.ExpectedDateBound("2026-10-16", sent, after: false)!, sent));
        Assert.False(due >= Boundary(TicketingClient.ExpectedDateBound("2026-10-16", sent, after: true)!, sent));
    }

    private static DateTimeOffset Boundary(string date, int sent) =>
        new DateTimeOffset(DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(sent);

    [Fact]
    public async Task A_list_without_a_date_filter_keeps_the_offset_as_it_is()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);

        await TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { Priority = "Low", TimezoneOffset = -5 }, Ct);

        Assert.Equal("-5", TestFactory.Query(handler.Requests[0].Uri)["timezone"]);
    }

    [Fact]
    public async Task The_default_zones_offset_is_negated_too()
    {
        // The test clock is 2026-01-15, when America/Chicago is UTC-6.
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);

        await TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { CreatedAfter = "2026-01-01" }, Ct);

        Assert.Equal("6", TestFactory.Query(handler.Requests[0].Uri)["timezone"]);
    }

    [Fact]
    public async Task List_tickets_refuses_a_time_of_day_and_sends_a_plain_date()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => tools.ListTickets(lastUpdateAfter: "2026-09-25T09:00:00", cancellationToken: Ct));
        Assert.Contains("whole days", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests); // nothing reached the API

        await tools.ListTickets(createdAfter: "2026-09-24", createdBefore: "2026-09-25", timezoneOffset: -5, cancellationToken: Ct);
        Dictionary<string, string> query = TestFactory.Query(handler.Requests.Single().Uri);
        Assert.Equal(("2026-09-24", "2026-09-25", "5"), (query["createdAfter"], query["createdBefore"], query["timezone"]));
    }

    [Fact]
    public async Task An_out_of_range_offset_is_still_refused_before_negating()
    {
        var handler = new FakeHttpHandler();

        await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { CreatedAfter = "2026-01-01", TimezoneOffset = 15 }, Ct));
        Assert.Empty(handler.Requests);
    }
}