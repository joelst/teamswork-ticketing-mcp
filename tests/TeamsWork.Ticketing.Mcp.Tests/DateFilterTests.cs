using System.Globalization;
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
/// How date filters reach the API (see TicketDateFilters for what the live API does with them). The default zone here
/// is America/Chicago and the test clock 2026-01-15, when it is UTC-6.
/// </summary>
public sealed class DateFilterTests
{
    private const string Empty = """{"items":[]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<Dictionary<string, string>> SentFor(TicketListQuery q)
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);
        await TestFactory.Client(handler).ListTicketsAsync(q, Ct);
        return TestFactory.Query(handler.Requests.Single().Uri);
    }

    // ---- Created and updated days: the caller's local days --------------------------------------------------------

    [Theory]
    [InlineData(-5, "5")]   // US Central daylight time: day D starts at D 05:00 UTC
    [InlineData(10, "-10")] // Sydney standard time: day D starts at D-1 14:00 UTC
    [InlineData(0, "0")]
    [InlineData(14, "-14")] // the far ends of the range negate to values the API accepts
    [InlineData(-12, "12")]
    public async Task A_created_or_updated_filter_sends_the_offset_negated(int offset, string sent)
    {
        Dictionary<string, string> query = await SentFor(new TicketListQuery { LastUpdateAfter = D("2026-09-25"), TimezoneOffset = offset });

        Assert.Equal(sent, query["timezone"]);
        Assert.Equal("2026-09-25", query["lastUpdateAfter"]);
    }

    [Theory]
    [InlineData("createdAfter")]
    [InlineData("createdBefore")]
    [InlineData("lastUpdateAfter")]
    [InlineData("lastUpdateBefore")]
    public async Task Each_created_or_updated_filter_is_sent_as_a_date_with_the_offset_negated(string filter)
    {
        DateOnly day = D("2026-03-02");
        TicketListQuery q = filter switch
        {
            "createdAfter" => new() { CreatedAfter = day },
            "createdBefore" => new() { CreatedBefore = day },
            "lastUpdateAfter" => new() { LastUpdateAfter = day },
            "lastUpdateBefore" => new() { LastUpdateBefore = day },
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };

        Dictionary<string, string> query = await SentFor(q with { TimezoneOffset = -3 }); // not the default zone's offset

        Assert.Equal("2026-03-02", query[filter]);
        Assert.Equal("3", query["timezone"]);
    }

    [Theory]
    [InlineData("2026-01-10", "6")] // a winter day: UTC-6
    [InlineData("2026-07-01", "5")] // a summer day asked for in winter: UTC-5 on that day, not today's UTC-6
    public async Task The_default_zones_offset_is_taken_on_the_filtered_day(string day, string sent)
    {
        Dictionary<string, string> query = await SentFor(new TicketListQuery { CreatedAfter = D(day) });

        Assert.Equal(sent, query["timezone"]);
    }

    [Fact]
    public async Task A_range_uses_the_offset_of_its_earliest_day()
    {
        Dictionary<string, string> query = await SentFor(new TicketListQuery { CreatedAfter = D("2026-01-10"), CreatedBefore = D("2026-07-01") });

        Assert.Equal("6", query["timezone"]);
    }

    [Fact]
    public void The_filter_descriptions_state_what_one_shared_offset_costs()
    {
        // The two behaviours above that one 'timezone' per request can't avoid are part of the tool contract, so
        // agents don't rely on more than the server can do.
        Assert.Contains("daylight-saving change the later end is an hour off", DateFilterText.CreatedBefore, StringComparison.Ordinal);
        Assert.Contains("daylight-saving change the later end is an hour off", DateFilterText.LastUpdateBefore, StringComparison.Ordinal);
        Assert.Contains("alongside a created or updated filter", DateFilterText.ExpectedDateAfter, StringComparison.Ordinal);
        Assert.Contains("alongside a created or updated filter", DateFilterText.ExpectedDateBefore, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_without_a_date_filter_keeps_the_offset_as_it_is()
    {
        Dictionary<string, string> query = await SentFor(new TicketListQuery { Priority = "Low", TimezoneOffset = -5 });

        Assert.Equal("-5", query["timezone"]);
        Assert.DoesNotContain(query.Keys, k => k.Contains("After", StringComparison.Ordinal) || k.Contains("Before", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_out_of_range_offset_is_refused_before_anything_is_sent()
    {
        var handler = new FakeHttpHandler();

        await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { CreatedAfter = D("2026-01-01"), TimezoneOffset = 15 }, Ct));
        Assert.Empty(handler.Requests);
    }

    // ---- Expected dates: calendar dates in the instance's zone ----------------------------------------------------

    // On the live API a ticket due 2026-10-15 was stored as 00:00 UTC when set at offset 0, and as 05:00 UTC when set
    // at -5. Both must count as due on the 15th in America/Chicago (UTC-5 then), whatever the caller's offset.
    private static readonly DateTimeOffset[] DueOn15th =
    [
        new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
        new(2026, 10, 15, 5, 0, 0, TimeSpan.Zero),
    ];

    [Theory]
    [InlineData(null)]
    [InlineData(-12)]
    [InlineData(-5)]
    [InlineData(5)]
    [InlineData(14)]
    public async Task An_expected_date_matches_its_calendar_day_whatever_the_callers_offset(int? offset)
    {
        foreach (DateTimeOffset due in DueOn15th)
        {
            Assert.True(await Matches(new() { ExpectedDateAfter = D("2026-10-15"), TimezoneOffset = offset }, due));
            Assert.False(await Matches(new() { ExpectedDateAfter = D("2026-10-16"), TimezoneOffset = offset }, due));
            Assert.True(await Matches(new() { ExpectedDateBefore = D("2026-10-16"), TimezoneOffset = offset }, due));
            Assert.False(await Matches(new() { ExpectedDateBefore = D("2026-10-15"), TimezoneOffset = offset }, due));
        }
    }

    [Fact]
    public async Task Expected_dates_alone_put_the_boundary_at_noon_of_the_day_before()
    {
        Dictionary<string, string> query = await SentFor(new TicketListQuery
        {
            ExpectedDateAfter = D("2026-10-15"),
            ExpectedDateBefore = D("2026-10-20"),
            TimezoneOffset = 9, // ignored: an expected date is a calendar date
        });

        // Noon on 2026-10-14 in Chicago (UTC-5) is 17:00 UTC: 2026-10-15 00:00 UTC less 7 hours.
        Assert.Equal(("-7", "2026-10-15", "2026-10-20"), (query["timezone"], query["expectedDateAfter"], query["expectedDateBefore"]));
    }

    [Fact]
    public async Task Alongside_a_created_filter_an_expected_date_is_matched_at_the_zones_midnight()
    {
        // The caller is in the instance's zone, so 'timezone' is 5 and the boundaries fall on Chicago midnights.
        Dictionary<string, string> query = await SentFor(new TicketListQuery
        {
            CreatedAfter = D("2026-10-01"),
            ExpectedDateAfter = D("2026-10-15"),
            ExpectedDateBefore = D("2026-10-20"),
            TimezoneOffset = -5,
        });

        Assert.Equal("5", query["timezone"]);
        Assert.Equal("2026-10-15", query["expectedDateAfter"]);  // 15th 05:00 UTC: a date due on the 15th, set in Chicago, is at it
        Assert.Equal("2026-10-19", query["expectedDateBefore"]); // 19th 05:00 UTC: the 19th is in, the 20th out
    }

    /// <summary>Whether the API, as observed live, would return a ticket due at <paramref name="due"/> for the query.</summary>
    private static async Task<bool> Matches(TicketListQuery q, DateTimeOffset due)
    {
        Dictionary<string, string> query = await SentFor(q);
        int timezone = int.Parse(query["timezone"], CultureInfo.InvariantCulture);
        bool ok = true;
        if (query.TryGetValue("expectedDateAfter", out string? after))
        {
            ok &= due >= Boundary(after, timezone);
        }

        if (query.TryGetValue("expectedDateBefore", out string? before))
        {
            ok &= due <= Boundary(before, timezone);
        }

        return ok;
    }

    private static DateTimeOffset Boundary(string date, int timezone) =>
        new DateTimeOffset(D(date).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(timezone);

    [Fact]
    public async Task An_offset_that_expected_dates_alone_dont_use_is_still_checked()
    {
        var handler = new FakeHttpHandler();

        await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery { ExpectedDateAfter = D("2026-10-15"), TimezoneOffset = 99 }, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Dates_at_the_ends_of_the_calendar_are_held_there_rather_than_throwing()
    {
        // The tools refuse these; the planner mustn't depend on that for any other caller.
        Dictionary<string, string> query = await SentFor(new TicketListQuery
        {
            ExpectedDateAfter = DateOnly.MinValue,
            ExpectedDateBefore = DateOnly.MaxValue,
            CreatedAfter = DateOnly.MinValue,
        });

        Assert.Equal("0001-01-01", query["expectedDateAfter"]);
        Assert.Equal("9999-12-30", query["expectedDateBefore"]); // with a created filter too, "before" takes the day before
    }

    // ---- Paging back by date after a scan stops early -------------------------------------------------------------

    private static string Tickets(params (string Id, string Created)[] rows) =>
        """{"items":[""" + string.Join(",", rows.Select(r => $$"""{"id":"{{r.Id}}","createdOn":"{{r.Created}}"}""")) + """],"itemCount":10}""";

    [Fact]
    public async Task A_scan_asks_for_newest_created_first_and_for_creation_times()
    {
        // The API's default order isn't by date (checked live), and the hint below depends on it.
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);

        await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery { Select = "id,status" }, t => t, 10, 10, Ct);

        Dictionary<string, string> query = TestFactory.Query(handler.Requests.Single().Uri);
        Assert.Equal(("createdDateTime", "DESC", "id,status,createdOn"), (query["orderBy"], query["order"], query["select"]));
    }

    [Fact]
    public async Task A_scan_keeps_an_order_the_caller_chose()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);

        await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery { OrderBy = "priority", Order = "ASC" }, t => t, 10, 10, Ct);

        Assert.Equal("priority", TestFactory.Query(handler.Requests.Single().Uri)["orderBy"]);
    }

    [Fact]
    public async Task A_truncated_scan_names_the_createdBefore_to_continue_with_in_local_days()
    {
        // 03:00 UTC on the 20th is still the 19th in US Central (UTC-5 then).
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Tickets(("a", "2026-09-25T12:00:00Z"), ("b", "2026-09-20T03:00:00Z")));
        var q = new TicketListQuery { TimezoneOffset = -5 };

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), q, t => t, 2, 2, Ct);
        string hint = TicketScan.TruncationHint(r, q, TestFactory.Client(new FakeHttpHandler()).TimeZones)!;

        Assert.True(r.Truncated);
        Assert.Contains("the oldest checked was created on 2026-09-19", hint, StringComparison.Ordinal);
        Assert.Contains("createdBefore '2026-09-20'", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]          // every ticket checked is from one day: the suggestion would read the same tickets again
    [InlineData("2026-09-19")]  // the suggestion (the 19th: oldest is the 18th) isn't earlier than the createdBefore given
    public async Task A_truncated_scan_says_when_a_date_cant_reach_the_rest(string? createdBefore)
    {
        (string, string)[] rows = createdBefore is null
            ? [("a", "2026-09-19T20:00:00Z"), ("b", "2026-09-19T15:00:00Z")]
            : [("a", "2026-09-19T20:00:00Z"), ("b", "2026-09-18T15:00:00Z")];
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Tickets(rows));
        var q = new TicketListQuery { TimezoneOffset = -5, CreatedBefore = createdBefore is null ? null : D(createdBefore) };

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), q, t => t, 2, 2, Ct);
        string hint = TicketScan.TruncationHint(r, q, TestFactory.Client(new FakeHttpHandler()).TimeZones)!;

        Assert.Contains("a date filter can't reach the rest", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("call again with createdBefore", hint, StringComparison.Ordinal);
    }

    // ---- The tools ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_tickets_refuses_a_time_of_day_and_sends_each_filter()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Empty);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => tools.ListTickets(lastUpdateAfter: "2026-09-25T09:00:00", cancellationToken: Ct));
        Assert.Contains("whole days", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests); // nothing reached the API

        await tools.ListTickets(
            createdAfter: "2026-09-24", createdBefore: "2026-09-25", lastUpdateAfter: "2026-09-20", lastUpdateBefore: "2026-09-26",
            expectedDateAfter: "2026-10-15", expectedDateBefore: "2026-10-20", timezoneOffset: -5, cancellationToken: Ct);
        Dictionary<string, string> query = TestFactory.Query(handler.Requests.Single().Uri);
        Assert.Equal(
            ("5", "2026-09-24", "2026-09-25", "2026-09-20", "2026-09-26", "2026-10-15", "2026-10-19"),
            (query["timezone"], query["createdAfter"], query["createdBefore"], query["lastUpdateAfter"], query["lastUpdateBefore"],
             query["expectedDateAfter"], query["expectedDateBefore"]));
    }

    public static TheoryData<string> ScanTools => ["list_my_tickets", "list_sla_risk", "count_tickets"];

    [Theory]
    [MemberData(nameof(ScanTools))]
    public async Task The_scan_tools_send_their_date_filters(string tool)
    {
        // list_sla_risk reads the instance's SLA settings first; SLA on, so it goes on to scan.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, SlaOnInstanceJson)
            .Enqueue(HttpStatusCode.OK, Empty);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new WorkloadTools(client, new FixedActor(Jane), cache, options);

        await (tool switch
        {
            "list_my_tickets" => tools.ListMyTickets(createdAfter: "2026-09-01", createdBefore: "2026-09-08", timezoneOffset: -5, cancellationToken: Ct),
            "list_sla_risk" => tools.ListSlaRisk(createdAfter: "2026-09-01", createdBefore: "2026-09-08", timezoneOffset: -5, cancellationToken: Ct),
            _ => tools.CountTickets(createdAfter: "2026-09-01", createdBefore: "2026-09-08", timezoneOffset: -5, cancellationToken: Ct),
        });

        Dictionary<string, string> query = TestFactory.Query(handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/tickets", StringComparison.Ordinal)).Uri);
        Assert.Equal(("2026-09-01", "2026-09-08", "5"), (query["createdAfter"], query["createdBefore"], query["timezone"]));
    }

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("1899-12-31")]
    [InlineData("9999-12-31")]
    public void A_date_outside_the_supported_years_is_refused(string value)
    {
        // Sending one can need the day before or after, which the ends of the calendar don't have.
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateFilter(value, "expectedDateAfter"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateOnly(value, "expectedDate"));
    }
}
