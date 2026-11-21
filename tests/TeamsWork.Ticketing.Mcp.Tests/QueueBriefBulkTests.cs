using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>
/// Phase 2: activities as the live API sends them, who a ticket is waiting on, get_queue, get_ticket_brief, and the
/// preview-and-confirm bulk update.
/// </summary>
public sealed class QueueBriefBulkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly FixedTimeProvider Clock = new(Now);

    private const string Requestor = """{"id":"r1","name":"Pat Example","email":"pat@example.test"}""";
    private const string Agent = """{"id":"u1","name":"Jane Doe","email":"jane@example.test"}""";
    private const string Cc = """{"id":"c1","name":"Sam Cc","email":"sam@example.test"}""";
    private const string Mailbox = """{"id":"helpdesk@example.test","name":"IT Helpdesk","email":"helpdesk@example.test"}""";
    private const string Rule = """{"id":"automation","name":"Automation","email":""}""";

    // ---- Activities as the live API sends them -------------------------------------------------------------------

    [Fact]
    public void A_live_activity_shows_its_author_time_and_kind()
    {
        Activity a = Parse<Activity>($$"""{"id":"a1","action":"commented","comment":"Thanks","user":{{Agent}},"createdOn":"2026-11-19T21:53:27.568Z","cc":[],"_ts":1}""");

        JsonObject o = TicketViews.Activity(a, includeHtml: false);

        Assert.Equal("Jane Doe <jane@example.test>", o["by"]!.GetValue<string>());
        Assert.Equal("2026-11-19T21:53:27Z", o["on"]!.GetValue<string>());
        Assert.Equal("reply", o["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"action":"resolved","comment":"Fixed it"}""", "resolved")]
    [InlineData("""{"action":"assigned","changes":[{"name":"assignee","oldValue":null,"newValue":"x"}]}""", "assigned")]
    [InlineData("""{"action":"automation_ticket_created","automation":{"ruleId":"r"}}""", "automation")]
    [InlineData("""{"action":"commented","comment":"Internal","isPrivate":true}""", "note")]
    [InlineData("""{"comment":"Old shape"}""", "reply")]
    public void An_activitys_kind_comes_from_what_happened(string json, string kind)
    {
        Assert.Equal(kind, TicketViews.Activity(Parse<Activity>(json), includeHtml: false)["kind"]!.GetValue<string>());
    }

    // ---- waitingOn (spec section 8) ------------------------------------------------------------------------------

    public static TheoryData<string, string?, string?> WaitingCases => new()
    {
        // The latest public reply by a person decides.
        { $$"""[{"action":"commented","comment":"Still broken","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-17T12:00:00Z"}]""", "requestor", "2026-11-17T12:00:00Z" },
        // Someone neither the requestor nor known staff (a cc, or staff the assignee list leaves out) doesn't say.
        { $$"""[{"action":"commented","comment":"Me too","user":{{Cc}},"createdOn":"2026-11-18T12:00:00Z"}]""", null, null },
        // Private notes, automation, and the help desk's mailbox aren't replies.
        { $$"""[{"action":"commented","comment":"note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"commented","comment":"We got your email","user":{{Mailbox}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"automation_reply","comment":"Auto","automation":{"ruleId":"x"},"user":{{Rule}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"}]""", "requestor", "2026-11-18T12:00:00Z" },
        // Nobody has replied: the assignee, since the ticket was created.
        { $$"""[{"action":"created","user":{{Requestor}},"createdOn":"2026-11-10T09:00:00Z"}]""", "assignee", "2026-11-10T09:00:00Z" },
    };

    [Theory]
    [MemberData(nameof(WaitingCases))]
    public void Waiting_on_follows_the_latest_public_reply_by_a_person(string history, string? waitingOn, string? since)
    {
        Waiting.State s = Waiting.Compute(Ticket("In Progress"), Parse<List<Activity>>(history), People(), resolved: false, complete: true, Now);

        Assert.Equal(waitingOn, s.WaitingOn);
        Assert.Equal(since, s.WaitingSince is DateTimeOffset at ? TicketViews.Time(at) : null);
    }

    [Theory]
    [InlineData("Waiting on requestor", "requestor")]
    [InlineData("Pending customer", "requestor")]
    [InlineData("Awaiting vendor", "thirdParty")]
    public void A_state_named_for_waiting_decides(string status, string waitingOn)
    {
        string history = $$"""[{"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""";

        Assert.Equal(waitingOn, Waiting.Compute(Ticket(status), Parse<List<Activity>>(history), People(), resolved: false, complete: true, Now).WaitingOn);
    }

    [Fact]
    public void A_resolved_ticket_waits_on_nobody()
    {
        string history = $$"""[{"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""";

        Assert.Equal("none", Waiting.Compute(Ticket("Resolved"), Parse<List<Activity>>(history), People(), resolved: true, complete: true, Now).WaitingOn);
    }

    [Fact]
    public void A_staff_requestors_reply_is_the_requestors()
    {
        // The requestor is checked before the agent list, since staff raise tickets too.
        Ticket t = Parse<Ticket>($$"""{"id":"t","status":"In Progress","requestor":{{Agent}},"assignee":{"id":"u2","name":"John Smith","email":"john@example.test"} }""");
        string history = $$"""[{"action":"commented","comment":"Any news?","user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"}]""";

        Assert.Equal("assignee", Waiting.Compute(t, Parse<List<Activity>>(history), People(), resolved: false, complete: true, Now).WaitingOn);
    }

    [Fact]
    public void A_reply_from_someone_with_an_object_id_and_no_email_is_a_persons()
    {
        // The live API records some replies (from Teams, say) with the person's Entra object ID and no email.
        const string Pat = "aaaaaaaa-0000-0000-0000-000000000001";
        Ticket t = Parse<Ticket>($$"""{"id":"t","status":"In Progress","requestor":{"id":"{{Pat}}","name":"Pat Example","email":"pat@example.test"},"assignee":{{Agent}}}""");
        string history = $$"""
            [{"action":"commented","comment":"It's still broken","user":{"id":"{{Pat}}","name":"Pat Example","email":""},"createdOn":"2026-11-19T10:00:00Z"},
             {"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"}]
            """;

        Waiting.State s = Waiting.Compute(t, Parse<List<Activity>>(history), People(), resolved: false, complete: true, Now);

        Assert.Equal(("assignee", "requestor", 0), (s.WaitingOn, s.LastPublic!.By, s.IdleDays));
    }

    [Fact]
    public void Staff_missing_from_the_assignee_list_count_as_agents_once_known()
    {
        const string Kim = """{"id":"k1","name":"Kim Staff","email":"kim@example.test"}""";
        string history = $$"""[{"action":"commented","comment":"Try this","user":{{Kim}},"createdOn":"2026-11-18T12:00:00Z"}]""";
        List<Activity> activities = Parse<List<Activity>>(history);

        Assert.Null(Waiting.Compute(Ticket("In Progress"), activities, People(), resolved: false, complete: true, Now).WaitingOn);   // not placed yet
        TicketPeople known = People().WithAgents([new TicketUser("k1", "Kim Staff", "kim@example.test")]);
        Assert.Equal("requestor", Waiting.Compute(Ticket("In Progress"), activities, known, resolved: false, complete: true, Now).WaitingOn);
    }

    [Fact]
    public void Idle_days_count_any_persons_activity_but_not_automation()
    {
        string history = $$"""
            [{"action":"automation_x","automation":{"ruleId":"x"},"user":{{Rule}},"createdOn":"2026-11-19T11:00:00Z"},
             {"action":"commented","comment":"note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-16T12:00:00Z"},
             {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-10T12:00:00Z"}]
            """;

        Assert.Equal(3, Waiting.Compute(Ticket("In Progress"), Parse<List<Activity>>(history), People(), resolved: false, complete: true, Now).IdleDays);
    }

    // ---- get_queue -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task My_queue_ranks_urgent_first_then_tickets_waiting_on_me()
    {
        var handler = new Router(r => r switch
        {
            { Path: var p } when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            { Path: var p } when p.EndsWith("/tickets", StringComparison.Ordinal) => $$"""
                {"items":[
                  {"id":"{{Id(1)}}","ticketNo":1,"title":"Waiting on me","status":"In Progress","priority":"2_Medium","assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-01T00:00:00Z","lastUpdatedOn":"2026-11-18T12:00:00Z"},
                  {"id":"{{Id(2)}}","ticketNo":2,"title":"Urgent but answered","status":"In Progress","priority":"4_Urgent","assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-15T00:00:00Z","lastUpdatedOn":"2026-11-19T10:00:00Z"},
                  {"id":"{{Id(3)}}","ticketNo":3,"title":"Someone else's","status":"Open","priority":"4_Urgent","assignee":{"id":"u2","name":"John Smith","email":"john@example.test"},"createdOn":"2026-11-15T00:00:00Z"}],
                 "itemCount":3}
                """,
            { Path: var p } when p.Contains(Id(1), StringComparison.Ordinal) =>
                $$"""{"items":[{"action":"commented","comment":"Still broken","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]}""",
            { Path: var p } when p.Contains(Id(2), StringComparison.Ordinal) =>
                $$"""{"items":[{"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-19T10:00:00Z"}]}""",
            _ => throw new InvalidOperationException(r.Path),
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(cancellationToken: Ct));
        JsonElement[] items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([2, 1], items.Select(i => i.GetProperty("ticketNo").GetInt32()));
        Assert.Equal(("requestor", "assignee"), (items[0].GetProperty("waitingOn").GetString(), items[1].GetProperty("waitingOn").GetString()));
        Assert.Equal(18, items[1].GetProperty("ageDays").GetInt32());
        Assert.Equal(1, items[1].GetProperty("idleDays").GetInt32());
        Assert.False(items[0].TryGetProperty("assignee", out _)); // always me, so left out
        Assert.Equal("Pat Example <pat@example.test>", items[0].GetProperty("requestor").GetString());
        Assert.Equal("Jane Doe <jane@example.test>", doc.RootElement.GetProperty("who").GetString());
    }

    [Fact]
    public async Task Twenty_queue_rows_fit_the_6_kb_budget()
    {
        string person(int i) => $$"""{"id":"p{{i}}","name":"Alexandra Montgomery{{i}}","email":"alexandra.montgomery{{i}}@example.com"}""";
        string tickets = "{\"items\":[" + string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""
            {"id":"{{Id(i)}}","ticketNo":{{2200 + i}},"title":"{{new string('t', 120)}}","status":"In Progress","priority":"3_Important",
             "assignee":{{Agent}},"requestor":{{person(i)}},"createdOn":"2026-11-01T00:00:00Z","lastUpdatedOn":"2026-11-18T12:00:00Z",
             "description":"{{new string('d', 2000)}}","customFields":{"x":"y"},"requestorUnseenEventCnt":2,"_ts":1}
            """)) + "],\"itemCount\":20}";
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => tickets,
            _ => $$"""{"items":[{"action":"commented","comment":"{{new string('c', 900)}}","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        string json = await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(cancellationToken: Ct);

        Assert.Equal(20, JsonDocument.Parse(json).RootElement.GetProperty("returned").GetInt32());
        Assert.True(json.Length <= 6 * 1024, $"{json.Length} characters");
    }

    [Fact]
    public async Task History_is_read_for_at_most_the_budgeted_rows()
    {
        string tickets = "{\"items\":[" + string.Join(",", Enumerable.Range(1, 30).Select(i =>
            $$"""{"id":"{{Id(i)}}","ticketNo":{{i}},"title":"T{{i}}","status":"Open","assignee":{{Agent}},"createdOn":"2026-11-01T00:00:00Z"}""")) + "],\"itemCount\":30}";
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => tickets,
            _ => """{"items":[]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue(include: ["waitingOn"], limit: 30, cancellationToken: Ct));

        Assert.Equal(QueueTools.HistoryBudget, handler.Requests.Count(r => r.Path.EndsWith("/activities", StringComparison.Ordinal)));
        Assert.Contains($"history of {QueueTools.HistoryBudget} of the 30 matches", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolved_since_a_day_reads_resolved_tickets_and_keeps_those_resolved_since()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => $$"""
                {"items":[
                  {"id":"{{Id(1)}}","ticketNo":1,"title":"Done today","status":"Resolved","assignee":{{Agent}},"lastResolutionOn":"2026-11-18T15:00:00Z"},
                  {"id":"{{Id(2)}}","ticketNo":2,"title":"Done long ago, commented today","status":"Resolved","assignee":{{Agent}},"lastResolutionOn":"2026-10-01T15:00:00Z"}],
                 "itemCount":2}
                """,
            _ => throw new InvalidOperationException(r.Path),
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue(state: "resolvedSince", since: "2026-11-18", include: [], timezoneOffset: -6, cancellationToken: Ct));

        JsonElement item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("ticketNo").GetInt32());
        Assert.Equal("2026-11-18T15:00:00Z", item.GetProperty("resolvedOn").GetString());
        Dictionary<string, string> q = TestFactory.Query(handler.Requests.Single().Uri);
        Assert.Equal(("true", "2026-11-18"), (q["isResolved"], q["lastUpdateAfter"]));
    }

    [Theory]
    [InlineData("resolvedSince", null)]
    [InlineData(null, "2026-11-18")]
    public async Task Resolved_since_and_since_go_together(string? state, string? since)
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => "{}"));

        await Assert.ThrowsAsync<McpException>(() =>
            new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(state: state, since: since, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_team_view_counts_by_assignee()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => $$"""
                {"items":[
                  {"id":"{{Id(1)}}","ticketNo":1,"title":"A","assignee":{{Agent}}},
                  {"id":"{{Id(2)}}","ticketNo":2,"title":"B","assignee":{{Agent}}},
                  {"id":"{{Id(3)}}","ticketNo":3,"title":"C"}],"itemCount":3}
                """,
            _ => throw new InvalidOperationException(r.Path),
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue("team", include: ["counts"], cancellationToken: Ct));

        JsonElement counts = doc.RootElement.GetProperty("counts");
        Assert.Equal((3, 1, 2), (counts.GetProperty("total").GetInt32(), counts.GetProperty("unassigned").GetInt32(),
            counts.GetProperty("byAssignee").GetProperty("Jane Doe <jane@example.test>").GetInt32()));
    }

    // ---- get_ticket_brief ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_brief_says_who_it_waits_on_and_leaves_automation_out()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) => $$"""
                {"items":[
                  {"id":"a3","action":"automation_x","automation":{"ruleId":"x"},"user":{{Rule}},"createdOn":"2026-11-19T11:00:00Z","changes":[{"name":"priority","oldValue":"2_Medium","newValue":"3_Important"}]},
                  {"id":"a2","action":"commented","comment":"Still broken","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"},
                  {"id":"a1","action":"created","user":{{Requestor}},"createdOn":"2026-11-10T12:00:00Z"}]}
                """,
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":2130,"title":"VPN","status":"In Progress","assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-10T12:00:00Z"} }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options, Clock).GetTicketBrief(Id(1), cancellationToken: Ct));
        JsonElement b = doc.RootElement;

        Assert.Equal(("assignee", "2026-11-18T12:00:00Z"), (b.GetProperty("waitingOn").GetString(), b.GetProperty("waitingSince").GetString()));
        Assert.Equal((9, 1), (b.GetProperty("ageDays").GetInt32(), b.GetProperty("idleDays").GetInt32()));
        Assert.Equal(["a2", "a1"], b.GetProperty("activities").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Contains("Left out: 1 by automation", b.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.False(b.TryGetProperty("attachments", out _)); // none, so left out
    }

    [Fact]
    public async Task A_brief_of_five_activities_fits_the_4_kb_budget()
    {
        string acts = "{\"items\":[" + string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""
            {"id":"act-{{i}}-0000000000000000","action":"commented","comment":"{{new string('w', 300)}}\n\nFrom: Someone\nSent: earlier\nSubject: Re: help\n{{new string('q', 2000)}}",
             "user":{{(i % 2 == 0 ? Agent : Requestor)}},"createdOn":"2026-11-18T12:00:00.1234567Z","cc":[],"_ts":1,"inboundProcessingId":"x"}
            """)) + "]}";
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) => acts,
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) =>
                """{"items":[{"id":"att-1","filename":"screenshot.png","isImage":true,"src":"https://storage.example.test/very/long/signed/url?sig=abc","createdBy":"Pat Example","createdDateTime":"2026-11-18T12:00:00Z"}]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""
                {"item":{"id":"{{Id(1)}}","ticketNo":2130,"title":"{{new string('t', 100)}}","status":"In Progress","priority":"2_Medium",
                 "assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-10T12:00:00Z","lastUpdatedOn":"2026-11-18T12:00:00Z",
                 "description":"{{new string('d', 600)}}","resolvedStatus":["Resolved","Closed"],"requestorUnseenEventCnt":7,
                 "workflow":[{"id":"In Progress","label":"In Progress","nextSteps":[{"targetId":"Resolved","recordComment":true}]},{"id":"Resolved","label":"Resolved","nextSteps":[]}]} }
                """,
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        string json = await new LookupTools(client, cache, options, Clock).GetTicketBrief(Id(1), cancellationToken: Ct);

        Assert.Equal(5, JsonDocument.Parse(json).RootElement.GetProperty("activities").GetArrayLength());
        Assert.True(json.Length <= 4 * 1024, $"{json.Length} characters");
    }

    [Fact]
    public async Task Private_notes_can_be_left_out_of_a_brief()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) => $$"""
                {"items":[{"id":"n","action":"commented","comment":"internal","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"},
                          {"id":"r","action":"commented","comment":"public","user":{{Requestor}},"createdOn":"2026-11-17T12:00:00Z"}]}
                """,
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":1,"title":"T","status":"Open","requestor":{{Requestor}} } }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options, Clock).GetTicketBrief(Id(1), includePrivate: false, cancellationToken: Ct));

        Assert.Equal(["r"], doc.RootElement.GetProperty("activities").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
    }

    // ---- Bulk update ---------------------------------------------------------------------------------------------

    private static string Bulk(int n, string title, string status, long ts, bool commentRequired = false) => $$"""
        {"id":"{{Id(n)}}","ticketNo":{{100 + n}},"title":"{{title}}","status":"{{status}}","_ts":{{ts}},"assignee":{{Agent}},
         "workflow":[{"id":"Open","label":"Open","nextSteps":[{"targetId":"In Progress"}]},
                     {"id":"In Progress","label":"In Progress","nextSteps":[{"targetId":"Resolved","transitionLabel":"Resolve","recordComment":{{(commentRequired ? "true" : "false")}}}]},
                     {"id":"Resolved","label":"Resolved","nextSteps":[]}]}
        """;

    private static string WeeklyChecks(bool commentRequired = false) =>
        "{\"items\":[" + string.Join(",",
            Bulk(1, "Weekly check: backups", "In Progress", 1000, commentRequired),
            Bulk(2, "Weekly check: patching", "In Progress", 2000, commentRequired),
            Bulk(3, "Weekly check: certificates", "Open", 3000, commentRequired),
            Bulk(4, "Printer jammed", "In Progress", 4000, commentRequired)) + "],\"itemCount\":4}";

    private static BulkChanges Resolve => new() { Status = "Resolved", Resolution = "fixed" };

    [Fact]
    public async Task A_preview_changes_nothing_and_says_what_would_change()
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        BulkTools bulk = Bulk(handler, new BulkStore(Clock));

        using JsonDocument doc = JsonDocument.Parse(await bulk.PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "weekly check:*" }, set: Resolve,
            comment: new BulkComment { Text = "Checks complete.", IsPrivate = false }, cancellationToken: Ct));
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal("Resolved", root.GetProperty("to").GetProperty("status").GetString());
        JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("In Progress", items[0].GetProperty("from").GetProperty("status").GetString());
        Assert.Contains(items, i => i.GetProperty("ticketNo").GetInt32() == 103 && i.TryGetProperty("blocked", out _)); // Open can't go straight to Resolved
        Assert.DoesNotContain(items, i => i.GetProperty("ticketNo").GetInt32() == 104);
        Assert.Equal(22, root.GetProperty("confirmToken").GetString()!.Length); // 128 random bits
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_step_that_records_a_comment_wont_make_a_private_one_public()
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks(commentRequired: true));
        BulkTools bulk = Bulk(handler, new BulkStore(Clock));

        using JsonDocument doc = JsonDocument.Parse(await bulk.PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Weekly check: b*" }, set: Resolve,
            comment: new BulkComment { Text = "internal", IsPrivate = true }, cancellationToken: Ct));

        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Contains("comment the requestor can see", doc.RootElement.GetProperty("items")[0].GetProperty("blocked").GetString(), StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("confirmToken", out _));
    }

    [Fact]
    public async Task Confirming_applies_the_change_and_skips_tickets_changed_since()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);

        var confirm = new Router(r => r switch
        {
            { Method: var m, Path: var p } when m == HttpMethod.Get && p.EndsWith(Id(1), StringComparison.Ordinal) => "{\"item\":" + Bulk(1, "Weekly check: backups", "In Progress", 1000) + "}",
            { Method: var m, Path: var p } when m == HttpMethod.Get && p.EndsWith(Id(2), StringComparison.Ordinal) => "{\"item\":" + Bulk(2, "Weekly check: patching", "In Progress", 2001) + "}",
            { Method: var m } when m == HttpMethod.Put => "{\"item\":" + Bulk(1, "Weekly check: backups", "Resolved", 1002) + "}",
            _ => throw new InvalidOperationException(r.Method + " " + r.Path),
        });

        using JsonDocument doc = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(token, Ct));
        JsonElement root = doc.RootElement;

        Assert.Equal([101], root.GetProperty("applied").EnumerateArray().Select(a => a.GetInt32()));
        JsonElement skipped = Assert.Single(root.GetProperty("skipped").EnumerateArray());
        Assert.Equal((102, "changedSincePreview"), (skipped.GetProperty("ticketNo").GetInt32(), skipped.GetProperty("reason").GetString()));
        CapturedRoute put = Assert.Single(confirm.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith($"/tickets/{Id(1)}/status", put.Path, StringComparison.Ordinal);
        Assert.Contains("\"comment\":\"Checks complete.\"", put.Body, StringComparison.Ordinal); // a public comment goes with the change
        Assert.Contains("\"resolution\":\"fixed\"", put.Body, StringComparison.Ordinal);
        Assert.Equal(2, Math.Abs(int.Parse(TestFactory.Query(put.Uri)["timezone"], System.Globalization.CultureInfo.InvariantCulture))); // the previewing caller's offset
        Assert.True(root.ToString().Length <= 400, root.ToString());
    }

    [Fact]
    public async Task Confirming_again_returns_the_first_result_and_changes_nothing()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        var confirm = new Router(r => r.Method == HttpMethod.Get
            ? "{\"item\":" + (r.Path.EndsWith(Id(1), StringComparison.Ordinal) ? Bulk(1, "Weekly check: backups", "In Progress", 1000) : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}"
            : "{\"item\":" + Bulk(1, "x", "Resolved", 9) + "}");
        BulkTools bulk = Bulk(confirm, store);

        string first = await bulk.BulkUpdate(token, Ct);
        int requests = confirm.Requests.Count;
        string again = await bulk.BulkUpdate(token, Ct);

        Assert.Equal(first, again);
        Assert.Equal(requests, confirm.Requests.Count);
    }

    [Fact]
    public async Task A_token_is_refused_to_anyone_else()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        var john = new ActingUser("u2", "John Smith", "john@example.test", ActingUserSource.DelegatedToken);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Bulk(new Router(_ => throw new InvalidOperationException()), store, john).BulkUpdate(token, Ct));

        Assert.Contains("issued to someone else", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_expires_after_15_minutes()
    {
        var time = new MovableTime(Now);
        var store = new BulkStore(time);
        string token = await PreviewWeeklyAsync(store);
        time.Advance(TimeSpan.FromMinutes(16));

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Bulk(new Router(_ => throw new InvalidOperationException()), store).BulkUpdate(token, Ct));

        Assert.Contains("expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_quota_runs_out_the_rest_gets_a_resume_token()
    {
        var store = new BulkStore(Clock);
        var caller = new KeyedCaller("me");
        string token = await PreviewWeeklyAsync(store, caller);

        // Two requests allowed: the first ticket's read and change; the second ticket's read is refused.
        var confirm = new Router(r => r.Method == HttpMethod.Get
            ? "{\"item\":" + Bulk(1, "Weekly check: backups", "In Progress", 1000) + "}"
            : "{\"item\":" + Bulk(1, "x", "Resolved", 9) + "}");
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 2));
        using var quota = new UpstreamQuota(opts);
        var client = new TicketingClient(new HttpClient(confirm), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, Clock),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, Clock, quota, caller);
        var bulk = new BulkTools(client, new FixedActor(Jane), new InstanceCache(opts, new TimeZoneOffsetResolver(opts, Clock), Clock), opts, store, caller);

        using JsonDocument doc = JsonDocument.Parse(await bulk.BulkUpdate(token, Ct));

        Assert.Equal([101], doc.RootElement.GetProperty("applied").EnumerateArray().Select(a => a.GetInt32()));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotNull(store.Find(doc.RootElement.GetProperty("resumeToken").GetString()!));
    }

    [Fact]
    public async Task A_filter_needs_a_condition_and_a_batch_has_a_cap()
    {
        string many = "{\"items\":[" + string.Join(",", Enumerable.Range(1, BulkTools.MaxTickets + 1).Select(i => Bulk(i, "Weekly check " + i, "In Progress", i))) + "],\"itemCount\":51}";
        BulkTools bulk = Bulk(new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : many), new BulkStore(Clock));

        McpException none = await Assert.ThrowsAsync<McpException>(() => bulk.PreviewBulkUpdate(filter: new BulkFilter(), set: Resolve, cancellationToken: Ct));
        McpException tooMany = await Assert.ThrowsAsync<McpException>(() =>
            bulk.PreviewBulkUpdate(filter: new BulkFilter { TitlePattern = "Weekly*" }, set: Resolve, cancellationToken: Ct));

        Assert.Contains("at least one condition", none.Message, StringComparison.Ordinal);
        Assert.Contains($"at most {BulkTools.MaxTickets}", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_must_say_whether_it_is_private()
    {
        BulkTools bulk = Bulk(new Router(_ => InstanceJson), new BulkStore(Clock));

        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            bulk.PreviewBulkUpdate(tickets: ["101"], comment: new BulkComment { Text = "hi" }, cancellationToken: Ct));

        Assert.Contains("isPrivate' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tickets_named_by_number_are_found_in_one_read()
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        BulkTools bulk = Bulk(handler, new BulkStore(Clock));

        using JsonDocument doc = JsonDocument.Parse(await bulk.PreviewBulkUpdate(tickets: ["#101", "102"], set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/tickets", StringComparison.Ordinal));
    }

    // ---- Review fixes --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_brief_names_only_what_it_left_out_from_among_what_it_shows()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) => $$"""
                {"items":[{"id":"r2","action":"commented","comment":"newest","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"},
                          {"id":"n1","action":"commented","comment":"internal","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-17T12:00:00Z"},
                          {"id":"r1","action":"commented","comment":"older","user":{{Requestor}},"createdOn":"2026-11-16T12:00:00Z"},
                          {"id":"n0","action":"commented","comment":"oldest note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-15T12:00:00Z"}]}
                """,
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":1,"title":"T","status":"Open","requestor":{{Requestor}} } }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options, Clock)
            .GetTicketBrief(Id(1), activities: 2, includePrivate: false, includeAutomation: true, cancellationToken: Ct));
        string hint = doc.RootElement.GetProperty("hint").GetString()!;

        Assert.Contains("Left out: 1 private note", hint, StringComparison.Ordinal); // n1, between the two shown; not n0, older than both
        Assert.DoesNotContain("automation", hint.Split('(')[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Steps:\nFrom: Settings > Network\nthen reset the adapter")]
    [InlineData("Done.\n--\nNext: check the logs")]
    [InlineData("Notes\n____________\nmore notes")]
    public void Ordinary_text_that_looks_like_a_quote_marker_is_kept(string text)
    {
        Assert.Equal(text, TicketViews.StripQuoted(text));
    }

    [Theory]
    [InlineData("Thanks.\n________________________________\nFrom: Pat <pat@example.test>\nSent: Monday\nTo: Help\nSubject: Re: help\nold")]
    [InlineData("Thanks.\nFrom: Pat <pat@example.test>\nDate: Monday\nSubject: Re: help\nold")]
    [InlineData("Thanks.\n-----Original Message-----\nold")]
    [InlineData("Thanks.\nOn Mon, 1 Jan 2026 at 10:00, Pat wrote:\n> old")]
    [InlineData("Thanks.\r\n-----Original Message-----\r\nold")]
    [InlineData("Thanks.\r\nOn Mon, 1 Jan 2026 at 10:00, Pat wrote:\r\n> old")]
    [InlineData("Thanks.\r\nFrom: Pat <pat@example.test>\r\nSent: Monday\r\nSubject: Re: help\r\nold")]
    public void A_real_quoted_reply_is_cut(string text)
    {
        Assert.StartsWith("Thanks. [quoted reply cut", TicketViews.StripQuoted(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_standup_list_reads_no_history_by_default()
    {
        var handler = new Router(r => r.Path.EndsWith("/tickets", StringComparison.Ordinal)
            ? $$"""{"items":[{"id":"{{Id(1)}}","ticketNo":1,"title":"Done","status":"Resolved","assignee":{{Agent}},"lastResolutionOn":"2026-11-18T15:00:00Z"}],"itemCount":1}"""
            : """{"items":[]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(state: "resolvedSince", since: "2026-11-18", cancellationToken: Ct);

        Assert.Equal(["/tickets"], handler.Requests.Select(r => r.Path[r.Path.LastIndexOf('/')..]));
    }

    private const string TwoTagCategories = """
        {"items":[{"id":"net","text":"Network","tags":[{"text":"VPN"},{"text":"Wi-Fi"}]},
                  {"id":"prod","text":"Product","tags":[{"text":"VPN"},{"text":"Printer"}]}]}
        """;

    private static string Tagged(int n, string tags) =>
        $$"""{"id":"{{Id(n)}}","ticketNo":{{100 + n}},"title":"T{{n}}","status":"Open","_ts":{{n}},"tags":{{tags}} }""";

    [Fact]
    public async Task A_tag_one_ticket_cant_resolve_blocks_that_ticket_not_the_preview()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/tags", StringComparison.Ordinal) => TwoTagCategories,
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => "{\"items\":[" + Tagged(1, "[\"VPN\"]") + "," + Tagged(2, "[]") + "],\"itemCount\":2}", // VPN is in both categories
        });

        using JsonDocument doc = JsonDocument.Parse(await Bulk(handler, new BulkStore(Clock)).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "T*" }, set: new BulkChanges { AddTags = [new TagRef { TagCategoryId = "prod", Text = "Printer" }] }, cancellationToken: Ct));

        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Contains(doc.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("ticketNo").GetInt32() == 101 && i.TryGetProperty("blocked", out _));
    }

    [Fact]
    public async Task The_same_tag_text_in_another_category_is_a_change()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/tags", StringComparison.Ordinal) => TwoTagCategories,
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => "{\"items\":[" + Tagged(1, """[{"tagCategoryId":"net","text":"VPN"}]""") + "],\"itemCount\":1}",
        });

        using JsonDocument doc = JsonDocument.Parse(await Bulk(handler, new BulkStore(Clock)).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "T*" }, set: new BulkChanges { AddTags = [new TagRef { TagCategoryId = "prod", Text = "VPN" }] }, cancellationToken: Ct));

        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_custom_workflow_ticket_already_in_the_state_is_unchanged_not_blocked()
    {
        string ticket = $$"""
            {"items":[{"id":"{{Id(1)}}","ticketNo":101,"title":"Custom","status":"st_7","isCustomWorkflow":true,"_ts":1,
              "workflow":[{"id":"st_7","label":"Waiting on customer","nextSteps":[{"targetId":"st_8"}]},{"id":"st_8","label":"Done","nextSteps":[]}]}],"itemCount":1}
            """;
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : ticket);

        using JsonDocument doc = JsonDocument.Parse(await Bulk(handler, new BulkStore(Clock)).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Custom" }, set: new BulkChanges { Status = "Waiting on customer" }, cancellationToken: Ct));

        Assert.Equal([101], doc.RootElement.GetProperty("unchanged").EnumerateArray().Select(u => u.GetInt32()));
        Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void Versions_match_on_the_fields_and_on_ts_when_both_have_it()
    {
        Ticket listed = Parse<Ticket>("""{"id":"t","status":"Open","priority":"2_Medium","tags":["VPN"],"lastUpdatedOn":"2026-11-18T12:00:00.1234567Z"}""");
        Ticket read = Parse<Ticket>("""{"id":"t","status":"Open","priority":"Medium","tags":[{"tagCategoryId":"net","text":"VPN"}],"lastUpdatedOn":"2026-11-18T12:00:00Z","_ts":5}""");
        Ticket sameSecond = Parse<Ticket>("""{"id":"t","status":"In Progress","priority":"Medium","tags":["VPN"],"lastUpdatedOn":"2026-11-18T12:00:00Z","_ts":5}""");
        Ticket commented = Parse<Ticket>("""{"id":"t","status":"Open","priority":"Medium","tags":["VPN"],"lastUpdatedOn":"2026-11-18T12:00:00Z","_ts":9}""");

        Assert.False(TicketVersion.Of(listed).Comparable(TicketVersion.Of(read)));    // one without _ts can't show no comment came in
        Assert.False(TicketVersion.Of(listed).Matches(TicketVersion.Of(read)));
        Assert.False(TicketVersion.Of(read).Matches(TicketVersion.Of(sameSecond)));   // changed in the same second: _ts alone would miss it
        Assert.False(TicketVersion.Of(read).Matches(TicketVersion.Of(commented)));    // a comment moves _ts only
    }

    [Fact]
    public async Task A_custom_workflow_status_change_sends_the_state_id_read_from_whole_tickets()
    {
        // isCustomWorkflow can't be selected, so a preview that changes status reads whole tickets to learn it. As on the
        // live API, whole list rows have no _ts (a selected row and a read of one ticket do), so it is read first.
        const string Fields = """
            "ticketNo":101,"title":"Custom","status":"st_7","isCustomWorkflow":true,
             "workflow":[{"id":"st_7","label":"Working","nextSteps":[{"targetId":"st_8"}]},{"id":"st_8","label":"Done","nextSteps":[]}]
            """;
        string whole = $$"""{"id":"{{Id(1)}}",{{Fields}} }""";
        string read = $$"""{"id":"{{Id(1)}}","_ts":1,{{Fields}} }""";
        var store = new BulkStore(Clock);
        var preview = new Router(r =>
            r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson
            : TestFactory.Query(r.Uri).ContainsKey("select") ? $$"""{"items":[{"id":"{{Id(1)}}","_ts":1}],"itemCount":1}"""
            : "{\"items\":[" + whole + "],\"itemCount\":1}");
        using JsonDocument doc = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Custom" }, set: new BulkChanges { Status = "Done" }, cancellationToken: Ct));
        var confirm = new Router(r => "{\"item\":" + read + "}");

        using JsonDocument result = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(doc.RootElement.GetProperty("confirmToken").GetString()!, Ct));

        List<CapturedRoute> lists = preview.Requests.Where(r => r.Path.EndsWith("/tickets", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lists.Count);
        Assert.StartsWith("id,_ts", TestFactory.Query(lists[0].Uri)["select"], StringComparison.Ordinal);   // _ts first
        Assert.False(TestFactory.Query(lists[1].Uri).ContainsKey("select"));
        Assert.Equal([101], result.RootElement.GetProperty("applied").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Contains("\"status\":\"st_8\"", confirm.Requests.Single(r => r.Method == HttpMethod.Put).Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deleted_category_doesnt_make_a_tag_ambiguous()
    {
        List<TagCategory> categories = Parse<ListResponse<TagCategory>>("""
            {"items":[{"id":"old","text":"Old network","deleted":true,"tags":[{"text":"Network"}]},
                      {"id":"net","text":"Network","tags":[{"text":"Network"}]}]}
            """).Items!.ToList();

        Assert.Equal("net", InstanceLookup.MatchTag(new TagRef { Text = "network" }, 0, categories).TagCategoryId);
    }

    [Fact]
    public void A_custom_state_named_for_waiting_is_found_by_its_label()
    {
        Ticket t = Parse<Ticket>($$"""
            {"id":"t","status":"9f1c","requestor":{{Requestor}},"assignee":{{Agent}},"createdOn":"2026-11-10T09:00:00Z",
             "workflow":[{"id":"9f1c","label":"Awaiting vendor","nextSteps":[]}]}
            """);

        Assert.Equal("thirdParty", Waiting.Compute(t, [], People(), resolved: false, complete: true, Now, WorkflowStatus.LabelOf(t)).WaitingOn);
    }

    [Fact]
    public void Without_a_reply_in_a_partial_history_who_it_waits_on_is_unknown()
    {
        List<Activity> notesOnly = Parse<List<Activity>>($$"""[{"action":"commented","comment":"note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"}]""");

        Assert.Null(Waiting.Compute(Ticket("In Progress"), notesOnly, People(), resolved: false, complete: false, Now).WaitingOn);
        Assert.Equal("assignee", Waiting.Compute(Ticket("In Progress"), notesOnly, People(), resolved: false, complete: true, Now).WaitingOn);
    }

    [Fact]
    public async Task A_brief_counts_the_callers_own_reply_as_staffs()
    {
        // Kim isn't in the assignee list and isn't this ticket's assignee, but is the one asking.
        var kim = new ActingUser("k1", "Kim Staff", "kim@example.test", ActingUserSource.DelegatedToken);
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) =>
                """{"items":[{"id":"a","action":"commented","comment":"Try this","user":{"id":"k1","name":"Kim Staff","email":"kim@example.test"},"createdOn":"2026-11-18T12:00:00Z"}]}""",
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":1,"title":"T","status":"Open","requestor":{{Requestor}},"assignee":{{Agent}} } }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options, Clock, new FixedActor(kim)).GetTicketBrief(Id(1), cancellationToken: Ct));

        Assert.Equal("requestor", doc.RootElement.GetProperty("waitingOn").GetString());
    }

    [Fact]
    public async Task An_assignee_seen_by_any_ticket_read_counts_as_staff_in_a_later_brief()
    {
        // Kim isn't in the assignee list. find_similar_tickets reads a ticket assigned to Kim; Kim then replies on another.
        const string Kim = """{"id":"k1","name":"Kim Staff","email":"kim@example.test"}""";
        var pat = new ActingUser("p1", "Pat Asker", "pat@example.test", ActingUserSource.DelegatedToken);
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) =>
                $$"""{"items":[{"id":"a","action":"commented","comment":"Try this","user":{{Kim}},"createdOn":"2026-11-18T12:00:00Z"}]}""",
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) =>
                $$"""{"items":[{"id":"{{Id(2)}}","ticketNo":2,"title":"Printer jam","status":"Open","assignee":{{Kim}} }],"itemCount":1}""",
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":1,"title":"T","status":"Open","requestor":{{Requestor}},"assignee":{{Agent}} } }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);
        var lookup = new LookupTools(client, cache, options, Clock, new FixedActor(pat));

        await lookup.FindSimilarTickets("Printer jam", cancellationToken: Ct);
        using JsonDocument doc = JsonDocument.Parse(await lookup.GetTicketBrief(Id(1), cancellationToken: Ct));

        Assert.Equal("requestor", doc.RootElement.GetProperty("waitingOn").GetString());
    }

    [Fact]
    public async Task The_limit_is_applied_after_ranking_with_history()
    {
        // Both Medium; the older one waits on the requestor, the newer one on me. With limit 1, mine comes first.
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => $$"""
                {"items":[
                  {"id":"{{Id(1)}}","ticketNo":1,"title":"Answered","status":"Open","priority":"2_Medium","assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-01T00:00:00Z","lastUpdatedOn":"2026-11-10T00:00:00Z"},
                  {"id":"{{Id(2)}}","ticketNo":2,"title":"Mine","status":"Open","priority":"2_Medium","assignee":{{Agent}},"requestor":{{Requestor}},"createdOn":"2026-11-01T00:00:00Z","lastUpdatedOn":"2026-11-18T00:00:00Z"}],
                 "itemCount":2}
                """,
            var p when p.Contains(Id(1), StringComparison.Ordinal) =>
                $$"""{"items":[{"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-10T00:00:00Z"}]}""",
            _ => $$"""{"items":[{"action":"commented","comment":"Still broken","user":{{Requestor}},"createdOn":"2026-11-18T00:00:00Z"}]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue(include: ["waitingOn"], limit: 1, cancellationToken: Ct));

        Assert.Equal(2, Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray()).GetProperty("ticketNo").GetInt32());
    }

    [Fact]
    public async Task A_truncated_queue_says_so_without_pointing_at_a_filter_it_lacks()
    {
        // A page of the two the scan may read, of three in all.
        string tickets = "{\"items\":[" + string.Join(",", Enumerable.Range(1, 2).Select(i =>
            $$"""{"id":"{{Id(i)}}","ticketNo":{{i}},"title":"T","status":"Open","assignee":{{Agent}},"createdOn":"2026-11-0{{i}}T00:00:00Z"}""")) + "],\"itemCount\":3}";
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => tickets), o => o.MaxScanTickets = 2);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(include: [], cancellationToken: Ct));
        string hint = doc.RootElement.GetProperty("hint").GetString()!;

        Assert.Contains("MaxScanTickets", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("createdBefore", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void One_caller_previewing_in_a_loop_pushes_out_only_their_own_tokens()
    {
        var store = new BulkStore(Clock);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        BulkStore.Batch Batch(string owner) => new(owner, Now + BulkStore.Lifetime, "b", plan, []);
        string theirs = store.Add(Batch("someone else"));

        List<string> mine = Enumerable.Range(0, BulkStore.MaxBatchesPerOwner + 5).Select(_ => store.Add(Batch("looping caller"))).ToList();

        Assert.NotNull(store.Find(theirs));
        Assert.Equal(BulkStore.MaxBatchesPerOwner, mine.Count(t => store.Find(t) is not null));
        Assert.NotNull(store.Find(mine[^1]));
    }

    [Fact]
    public async Task Tickets_known_by_uuid_are_read_without_a_scan()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith(Id(1), StringComparison.Ordinal) => "{\"item\":" + Bulk(1, "A", "In Progress", 1) + "}",
            var p when p.EndsWith(Id(2), StringComparison.Ordinal) => "{\"item\":" + Bulk(2, "B", "In Progress", 2) + "}",
            _ => throw new InvalidOperationException(r.Path),
        });

        using JsonDocument doc = JsonDocument.Parse(await Bulk(handler, new BulkStore(Clock))
            .PreviewBulkUpdate(tickets: [Id(1), Id(2)], set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/tickets", StringComparison.Ordinal));
    }

    // ---- Review round 3 ------------------------------------------------------------------------------------------

    [Fact]
    public void A_batch_being_confirmed_is_never_evicted_and_an_evicted_one_never_starts()
    {
        var store = new BulkStore(Clock);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        BulkStore.Batch Batch() => new("caller", Now + BulkStore.Lifetime, "b", plan, []);
        BulkStore.Batch running = Batch();
        string token = store.Add(running);
        Assert.True(running.TryStart());

        List<BulkStore.Batch> later = Enumerable.Range(0, BulkStore.MaxBatchesPerOwner + 3).Select(_ => Batch()).ToList();
        later.ForEach(b => store.Add(b));

        Assert.Same(running, store.Find(token));                       // running: kept although it is the oldest
        BulkStore.Batch evicted = Assert.Single(later.Where(b => b.IsEvicted).Take(1));
        Assert.False(evicted.TryStart());                               // and an evicted one can't then be confirmed
    }

    [Fact]
    public void Tag_versions_tell_one_tag_from_two_and_compare_categories_when_both_have_them()
    {
        TicketVersion Of(string tags) => TicketVersion.Of(Parse<Ticket>($$"""{"id":"t","status":"Open","_ts":5,"tags":{{tags}} }"""));

        Assert.False(Of("""["Network,VPN"]""").Matches(Of("""["Network","VPN"]""")));
        Assert.False(Of("""[{"tagCategoryId":"net","text":"VPN"}]""").Matches(Of("""[{"tagCategoryId":"prod","text":"VPN"}]""")));
        Assert.True(Of("""["VPN"]""").Matches(Of("""[{"tagCategoryId":"net","text":"VPN"}]""")));   // one read without categories: texts decide (both have _ts)
    }

    [Fact]
    public async Task An_interrupted_confirmation_keeps_what_it_did_for_the_retry()
    {
        // Priority and a status change with no comment: both PUTs, which a cancel can cut off after they were sent.
        var store = new BulkStore(Clock);
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        using JsonDocument previewed = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Weekly check:*" }, set: Resolve with { Priority = "Urgent" }, cancellationToken: Ct));
        string token = previewed.RootElement.GetProperty("confirmToken").GetString()!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var confirm = new Router(r =>
        {
            if (r.Method == HttpMethod.Put && r.Path.EndsWith("/status", StringComparison.Ordinal))
            {
                cts.Cancel();   // the client gives up while the first ticket's status change is out
                throw new OperationCanceledException(cts.Token);
            }

            return "{\"item\":" + (r.Path.Contains(Id(1), StringComparison.Ordinal) ? Bulk(1, "Weekly check: backups", "In Progress", 1000) : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}";
        });
        BulkTools bulk = Bulk(confirm, store);

        try
        {
            await bulk.BulkUpdate(token, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // How the cancelled call ends doesn't matter: the retry below is what the caller sees.
        }

        using JsonDocument doc = JsonDocument.Parse(await bulk.BulkUpdate(token, Ct));

        string error = Assert.Single(doc.RootElement.GetProperty("failed").EnumerateArray()).GetProperty("error").GetString()!;
        Assert.Contains("fields changed; the status change may or may not have been", error, StringComparison.Ordinal);
        Assert.NotNull(store.Find(doc.RootElement.GetProperty("resumeToken").GetString()!));   // ticket 102, not reached
    }

    [Fact]
    public async Task A_failed_write_that_may_have_been_made_says_to_check_the_ticket()
    {
        var store = new BulkStore(Clock);
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        using JsonDocument previewed = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Weekly check:*" }, set: Resolve, cancellationToken: Ct));
        string token = previewed.RootElement.GetProperty("confirmToken").GetString()!;
        var confirm = new Router(r =>
            r.Method == HttpMethod.Put && r.Path.Contains(Id(1), StringComparison.Ordinal)
                ? """{"item":null,"error":true,"message":"Something failed"}"""   // received, so it may have been carried out
                : "{\"item\":" + (r.Path.Contains(Id(1), StringComparison.Ordinal) ? Bulk(1, "Weekly check: backups", "In Progress", 1000) : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}");

        using JsonDocument doc = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(token, Ct));

        string error = Assert.Single(doc.RootElement.GetProperty("failed").EnumerateArray()).GetProperty("error").GetString()!;
        Assert.Contains("Something failed", error, StringComparison.Ordinal);
        Assert.Contains("may have been made anyway", error, StringComparison.Ordinal);
        Assert.Contains("get_ticket_brief", error, StringComparison.Ordinal);
        Assert.Single(doc.RootElement.GetProperty("applied").EnumerateArray());
    }

    private const string FinalStateGraph = """
        {"ticket":[{"nodes":[{"id":"Open","type":"textFieldNode","data":{"label":"Open"}},{"id":"Closed","type":"textFieldNode","data":{"label":"Closed"}}],
                    "edges":[{"id":"e1","label":"Close","source":"Open","target":"Closed"}]}]}
        """;

    [Fact]
    public void A_final_state_in_the_instance_graph_has_no_way_out()
    {
        Ticket closed = Parse<Ticket>("""{"id":"t","status":"Closed"}""");

        McpException ex = Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(closed, "Open", null, Parse<JsonElement>(FinalStateGraph)));

        Assert.Contains("final state", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_name_states_that_share_a_label_by_id()
    {
        JsonElement graph = Parse<JsonElement>("""
            {"ticket":[{"nodes":[{"id":"a1","type":"textFieldNode","data":{"label":"Review"}},{"id":"a2","type":"textFieldNode","data":{"label":"Review"}},
                                 {"id":"Done","type":"textFieldNode","data":{"label":"Done"}}],
                        "edges":[{"id":"e1","source":"a1","target":"Done"},{"id":"e2","source":"a2","target":"Done"}]}]}
            """);

        JsonNode step = WorkflowStatus.Compact(graph)![0]!["steps"]![0]!;

        Assert.Equal(("Review (ID a1)", "Done"), (step["from"]!.GetValue<string>(), step["to"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("""{"isFrtBreached":false}""", null)]
    [InlineData("""{"isFrtBreached":false,"isRtBreached":false,"isFrtEscalated":false,"isRtEscalated":false}""", "ok")]
    [InlineData("""{"isRtBreached":true}""", "rtBreached")]
    public void Sla_is_ok_only_when_all_four_flags_say_so(string flags, string? expected)
    {
        Assert.Equal(expected, TicketViews.Sla(Parse<Ticket>(flags)));
    }

    [Fact]
    public void An_unseen_counter_that_isnt_a_number_is_ignored()
    {
        JsonObject detail = TicketViews.Detail(Parse<Ticket>("""{"id":"t","title":"T","requestorUnseenEventCnt":"many"}"""), includeHtml: false);

        Assert.False(detail.ContainsKey("requestorUnseenUpdates"));
    }

    [Theory]
    [InlineData("Closed", "none")]
    [InlineData("In Progress", "assignee")]
    [InlineData("Escalated to L3", null)]   // a custom state: resolved or not isn't known
    public void An_unstated_resolution_is_read_from_a_default_state_or_left_unknown(string status, string? waitingOn)
    {
        string history = $$"""[{"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""";

        Assert.Equal(waitingOn, Waiting.Compute(Ticket(status), Parse<List<Activity>>(history), People(), resolved: null, complete: true, Now).WaitingOn);
    }

    [Fact]
    public async Task MaxChars_returns_the_whole_description_and_activity_text()
    {
        string text = new string('x', 1500);
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/activities", StringComparison.Ordinal) =>
                $$"""{"items":[{"id":"a","action":"commented","comment":"{{text}}","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]}""",
            var p when p.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[]}""",
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            _ => $$"""{"item":{"id":"{{Id(1)}}","ticketNo":1,"title":"T","status":"Open","description":"{{text}}","requestor":{{Requestor}} } }""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument brief = JsonDocument.Parse(await new LookupTools(client, cache, options, Clock).GetTicketBrief(Id(1), maxChars: 20000, cancellationToken: Ct));
        using JsonDocument page = JsonDocument.Parse(await new ActivityTools(client, new FixedActor(Jane), cache, options).ListTicketActivities(Id(1), maxChars: 20000, cancellationToken: Ct));

        Assert.Equal(text, brief.RootElement.GetProperty("description").GetString());
        Assert.Equal(text, brief.RootElement.GetProperty("activities")[0].GetProperty("text").GetString());
        Assert.Equal(text, page.RootElement.GetProperty("items")[0].GetProperty("text").GetString());
    }

    private static string Rows(int count, Func<int, string> extra) =>
        "{\"items\":[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{"id":"{{Id(i)}}","ticketNo":{{i}},"title":"T{{i}}","status":"Open","assignee":{{Agent}}{{extra(i)}} }""")) + "],\"itemCount\":" + count + "}";

    [Fact]
    public async Task A_large_configured_page_size_doesnt_lift_the_queues_cap()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => Rows(60, _ => "")), o => (o.DefaultPageSize, o.MaxPageSize) = (100, 100));

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(include: [], sort: "oldest", cancellationToken: Ct));

        Assert.Equal(50, doc.RootElement.GetProperty("returned").GetInt32());
    }

    [Fact]
    public async Task A_resolved_ticket_without_a_resolution_time_isnt_counted_as_resolved_since()
    {
        string tickets = Rows(2, i => i == 1 ? ",\"lastResolutionOn\":\"2026-11-18T15:00:00Z\"" : "");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => tickets));

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue(state: "resolvedSince", since: "2026-11-18", cancellationToken: Ct));

        Assert.Equal([1], doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("ticketNo").GetInt32()));
        Assert.Contains("1 resolved tickets updated since then have no resolution time", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    private sealed class NoCaller : IActingUserProvider
    {
        public ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken) => throw new ActingUserException("No user and no service account.");
    }

    [Fact]
    public async Task An_app_only_caller_without_a_service_account_can_read_the_team_queue()
    {
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => Rows(1, _ => ",\"createdOn\":\"2026-11-10T00:00:00Z\",\"requestor\":" + Requestor),
            _ => $$"""{"items":[{"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new NoCaller(), cache, options, Clock).GetQueue("team", include: ["waitingOn"], cancellationToken: Ct));

        Assert.Equal("assignee", doc.RootElement.GetProperty("items")[0].GetProperty("waitingOn").GetString());
    }

    [Fact]
    public async Task An_open_queue_sorted_by_update_goes_by_the_last_update_not_an_old_resolution()
    {
        string tickets = Rows(2, i => i == 1
            ? ",\"lastUpdatedOn\":\"2026-11-19T10:00:00Z\",\"lastResolutionOn\":\"2026-10-01T00:00:00Z\""   // reopened today
            : ",\"lastUpdatedOn\":\"2026-11-18T10:00:00Z\"");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => tickets));

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(include: [], sort: "updated", cancellationToken: Ct));

        Assert.Equal([1, 2], doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("ticketNo").GetInt32()));
    }

    [Fact]
    public async Task Tickets_waiting_on_the_assignee_rank_by_how_long_they_have_waited()
    {
        // Both wait on me. Ticket 1 waited since the 9th but has a note today; ticket 2 waited since the 17th, untouched.
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => Rows(2, i => ",\"priority\":\"2_Medium\",\"requestor\":" + Requestor + ",\"lastUpdatedOn\":\"2026-11-19T10:00:00Z\""),
            var p when p.Contains(Id(1), StringComparison.Ordinal) => $$"""
                {"items":[{"action":"commented","comment":"note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-19T10:00:00Z"},
                          {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-09T10:00:00Z"}]}
                """,
            _ => $$"""{"items":[{"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-17T10:00:00Z"}]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue(include: ["waitingOn"], cancellationToken: Ct));

        Assert.Equal([1, 2], doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("ticketNo").GetInt32()));
    }

    // ---- Review round 4 ------------------------------------------------------------------------------------------

    [Fact]
    public void A_confirmation_outlives_the_deadline_and_its_result_stays_for_a_while()
    {
        var time = new MovableTime(Now);
        var store = new BulkStore(time);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        var running = new BulkStore.Batch("caller", Now + BulkStore.Lifetime, "b", plan, []);
        var idle = new BulkStore.Batch("caller", Now + BulkStore.Lifetime, "c", plan, []);
        string runningToken = store.Add(running), idleToken = store.Add(idle);
        Assert.True(running.TryStart());

        time.Advance(BulkStore.Lifetime + TimeSpan.FromMinutes(1));
        Assert.Same(running, store.Find(runningToken));   // started before the deadline, still writing after it
        Assert.Null(store.Find(idleToken));               // never confirmed: gone at the deadline
        Assert.False(idle.TryStart());

        running.Finish(new JsonObject { ["ok"] = true }, time.GetUtcNow());
        time.Advance(BulkStore.Lifetime - TimeSpan.FromMinutes(1));
        Assert.Same(running, store.Find(runningToken));   // its result can still be read
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.Find(runningToken));
    }

    [Fact]
    public async Task Previews_racing_each_other_dont_get_past_the_per_caller_cap()
    {
        var store = new BulkStore(Clock);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);

        string[] tokens = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
            store.Add(new BulkStore.Batch("caller", Now + BulkStore.Lifetime, "b", plan, [])), Ct)));

        Assert.Equal(BulkStore.MaxBatchesPerOwner, tokens.Count(t => store.Find(t) is not null));
    }

    [Fact]
    public void A_same_second_rename_or_new_description_is_a_change()
    {
        TicketVersion Of(string extra) => TicketVersion.Of(Parse<Ticket>($$"""{"id":"t","status":"Open","_ts":5,"lastUpdatedOn":"2026-11-18T12:00:00.100Z"{{extra}} }"""));

        Assert.False(Of(""","title":"Weekly check: backups" """).Matches(Of(""","title":"Backups (moved)" """)));
        Assert.False(Of(""","description":"old" """).Matches(Of(""","description":"new" """)));
        Assert.True(Of("").Matches(Of(""","description":"only one read has it" """)));   // a list row carries none
        Assert.False(Of("").Matches(TicketVersion.Of(Parse<Ticket>("""{"id":"t","status":"Open","_ts":5,"lastUpdatedOn":"2026-11-18T12:00:00.900Z"}"""))));
    }

    [Fact]
    public void A_note_with_from_and_to_lines_isnt_cut_as_a_quoted_email()
    {
        const string Note = "Network route:\nFrom: 10.1.1.1\nTo: 10.2.2.2\nPort: 443";

        Assert.Equal(Note, TicketViews.StripQuoted(Note));
    }

    [Fact]
    public void The_whole_text_keeps_the_quoted_reply()
    {
        var a = new Activity { Id = "a", Comment = "Thanks.\nFrom: Pat <pat@example.test>\nSent: Monday\nSubject: Re: help\nold message" };

        Assert.EndsWith("old message", TicketViews.Activity(a, includeHtml: false, TicketViews.FullTextChars)["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("quoted reply cut", TicketViews.Activity(a, includeHtml: false)["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_ticket_takes_the_graph_with_its_state_id_over_one_that_only_shares_the_label()
    {
        JsonElement graphs = Parse<JsonElement>("""
            {"ticket":[{"nodes":[{"id":"Open","type":"textFieldNode","data":{"label":"Open"}},{"id":"Resolved","type":"textFieldNode","data":{"label":"Resolved"}}],
                        "edges":[{"id":"e1","source":"Open","target":"Resolved"}]},
                       {"nodes":[{"id":"c-open","type":"textFieldNode","data":{"label":"Open"}},{"id":"c-done","type":"textFieldNode","data":{"label":"Done"}}],
                        "edges":[{"id":"e2","source":"c-open","target":"c-done"}]}]}
            """);

        IReadOnlyList<WorkflowStatus.NextStatus>? next = WorkflowStatus.AllowedNext(Parse<Ticket>("""{"id":"t","status":"Open"}"""), graphs);

        Assert.Equal(["Resolved"], next!.Select(n => n.Status));
    }

    [Theory]
    [InlineData("Waiting on assignee", "assignee")]
    [InlineData("Pending agent", "assignee")]
    [InlineData("Waiting on customer", "requestor")]
    public void A_waiting_state_that_names_the_help_desk_waits_on_the_assignee(string status, string waitingOn)
    {
        Assert.Equal(waitingOn, Waiting.Compute(Ticket(status), [], People(), resolved: false, complete: true, Now).WaitingOn);
    }

    [Fact]
    public void A_custom_state_labelled_closed_doesnt_settle_an_unstated_resolution()
    {
        Ticket t = Parse<Ticket>($$"""{"id":"t","status":"st_9","isCustomWorkflow":true,"requestor":{{Requestor}},"assignee":{{Agent}}}""");

        Assert.Null(Waiting.Compute(t, [], People(), resolved: null, complete: true, Now, statusLabel: "Closed").WaitingOn);
    }

    [Fact]
    public void A_write_answered_with_only_an_activity_id_still_names_the_ticket()
    {
        Guid ticket = Guid.Parse(Id(7));

        TicketViews.WriteAck ack = TicketViews.Ack(new CommentActivity { ActivityId = "act-1" }, Jane, ticket);

        Assert.Equal((Id(7), "act-1"), (ack.Id, ack.ActivityId));
    }

    [Fact]
    public async Task Returned_rows_whose_history_wasnt_read_are_said_so()
    {
        string tickets = Rows(30, _ => ",\"createdOn\":\"2026-11-01T00:00:00Z\"");
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => tickets,
            _ => """{"items":[]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock)
            .GetQueue(include: ["waitingOn", "idleDays"], sort: "oldest", limit: 30, cancellationToken: Ct));

        Assert.Contains("5 rows have no waitingOn, idleDays", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_ranking_input_a_lean_include_leaves_out_is_said()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(r =>
            r.Path.EndsWith("/instance", StringComparison.Ordinal) ? NewToolsTests.SlaOnInstanceJson : Rows(1, _ => "")));
        var queue = new QueueTools(client, new FixedActor(Jane), cache, options, Clock);

        using JsonDocument idle = JsonDocument.Parse(await queue.GetQueue(include: [], sort: "idle", cancellationToken: Ct));
        using JsonDocument urgency = JsonDocument.Parse(await queue.GetQueue(include: ["counts"], cancellationToken: Ct));

        Assert.Contains("include idleDays", idle.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
        string hint = urgency.RootElement.GetProperty("hint").GetString()!;
        Assert.Contains("include sla", hint, StringComparison.Ordinal);
        Assert.Contains("include waitingOn", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_assignees_with_one_name_and_no_email_are_counted_apart()
    {
        string tickets = "{\"items\":[" +
            $$"""{"id":"{{Id(1)}}","ticketNo":1,"title":"A","assignee":{"id":"x1","name":"Sam","email":""} },""" +
            $$"""{"id":"{{Id(2)}}","ticketNo":2,"title":"B","assignee":{"id":"x2","name":"Sam","email":""} }""" + "],\"itemCount\":2}";
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(new Router(_ => tickets));

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue("team", include: ["counts"], cancellationToken: Ct));

        Assert.Equal(2, doc.RootElement.GetProperty("counts").GetProperty("byAssignee").EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    // ---- Review round 6 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_whole_ticket_preview_blocks_a_ticket_whose_ts_wasnt_read_and_keeps_its_description()
    {
        string Whole(int n, string description) => $$"""{"id":"{{Id(n)}}","ticketNo":{{100 + n}},"title":"T{{n}}","status":"Open","description":"{{description}}"}""";
        var store = new BulkStore(Clock);
        var preview = new Router(r =>
            r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson
            : TestFactory.Query(r.Uri).ContainsKey("select") ? $$"""{"items":[{"id":"{{Id(1)}}","_ts":1}],"itemCount":1}"""   // ticket 2 came in after
            : "{\"items\":[" + Whole(1, "before") + "," + Whole(2, "x") + "],\"itemCount\":2}");
        using JsonDocument doc = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "T*" }, set: new BulkChanges { Status = "In Progress" }, cancellationToken: Ct));

        JsonElement blocked = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.TryGetProperty("blocked", out _));
        Assert.Equal(102, blocked.GetProperty("ticketNo").GetInt32());

        // The description was edited, in the same second and without moving lastUpdatedOn: the preview kept its hash.
        var confirm = new Router(r => "{\"item\":" + $$"""{"id":"{{Id(1)}}","ticketNo":101,"title":"T1","status":"Open","description":"after","_ts":1}""" + "}");
        using JsonDocument result = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(doc.RootElement.GetProperty("confirmToken").GetString()!, Ct));

        Assert.Equal("changedSincePreview", result.RootElement.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain(confirm.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void Finished_batches_keep_their_place_until_their_result_lapses_and_a_full_store_refuses_previews()
    {
        var clock = new MovableTime(Now);
        var store = new BulkStore(clock);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        BulkStore.Batch Batch() => new("caller", clock.GetUtcNow() + BulkStore.Lifetime, "b", plan, []);
        var done = Enumerable.Range(0, BulkStore.MaxBatchesPerOwner).Select(_ => Batch()).ToList();
        List<string> tokens = done.Select(b => store.Add(b)).ToList();
        done.ForEach(b => { Assert.True(b.TryStart()); b.Finish(new JsonObject(), clock.GetUtcNow()); });

        Assert.Throws<McpException>(() => store.Add(Batch()));            // no unconfirmed batch to make room with
        Assert.All(tokens, t => Assert.NotNull(store.Find(t)));            // every result can still be had
        Assert.NotNull(store.Find(store.Add(Batch(), resume: true)));      // the rest of a confirmed batch is always kept

        clock.Advance(BulkStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.NotNull(store.Find(store.Add(Batch())));                    // results lapsed: room again
    }

    [Fact]
    public async Task An_unexpected_failure_finishes_the_batch_so_a_retry_isnt_told_it_is_still_running()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        var confirm = new Router(_ => throw new InvalidOperationException("unexpected"));

        await Assert.ThrowsAnyAsync<Exception>(() => Bulk(confirm, store).BulkUpdate(token, Ct));
        using JsonDocument retry = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(token, Ct));

        Assert.False(retry.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Removing_a_tag_the_ticket_has_in_two_categories_needs_the_category()
    {
        List<TicketTag> current = [new("net", "VPN"), new("prod", "VPN")];
        List<TagCategory> categories = Parse<ListResponse<TagCategory>>("""{"items":[{"id":"net","text":"Network"},{"id":"prod","text":"Products"}]}""").Items!.ToList();

        McpException ex = Assert.Throws<McpException>(() => TagEdits.Apply(current, [], [new TagRef { Text = "vpn" }], categories));
        (List<TicketTag> tags, _) = TagEdits.Apply(current, [], [new TagRef { Text = "VPN", TagCategoryId = "Products" }], categories);

        Assert.Contains("Network, Products", ex.Message, StringComparison.Ordinal);
        Assert.Equal([new TicketTag("net", "VPN")], tags);
    }

    [Fact]
    public void Full_text_keeps_its_indentation_and_its_quoted_reply_note_points_where_to_get_it()
    {
        Activity a = Parse<Activity>("""{"id":"a","action":"commented","comment":"    indented code\n    more","createdOn":"2026-11-18T12:00:00Z"}""");

        Assert.Equal("    indented code\n    more", TicketViews.Activity(a, includeHtml: false, TicketViews.FullTextChars)["text"]!.GetValue<string>());
        Assert.Equal("indented code\n    more", TicketViews.Activity(a, includeHtml: false)["text"]!.GetValue<string>());
        Assert.EndsWith("[quoted reply cut; list_ticket_activities with maxChars=20000 has it]",
            TicketViews.StripQuoted("Thanks.\r\nFrom: Pat\r\nSent: Monday\r\nTo: Help desk\r\nSubject: Re: help\r\n\r\nOld text"), StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_writing_from_a_staff_address_with_another_object_id_isnt_staff()
    {
        string staff = Guid.NewGuid().ToString(), stranger = Guid.NewGuid().ToString();
        TicketPeople people = TicketPeople.From(null, TestFactory.Options()).WithAgents([new TicketUser(staff, "Jane", "jane@example.test")]);

        Assert.True(people.IsAgent(new TicketUser(staff.ToUpperInvariant(), "Jane", "other@example.test")));
        Assert.False(people.IsAgent(new TicketUser(stranger, "Someone", "jane@example.test")));
        Assert.True(people.IsAgent(new TicketUser("", "Jane", "jane@example.test")));                       // no ID: the email decides
        Assert.True(TicketPeople.From(null, TestFactory.Options()).WithAgents([new TicketUser("desk@example.test", "Desk", "desk@example.test")])
            .IsAgent(new TicketUser(stranger, "Someone", "desk@example.test")));                        // an agent known by email alone
    }

    [Fact]
    public void A_latest_person_activity_without_a_time_leaves_idle_days_unknown()
    {
        List<Activity> history = Parse<List<Activity>>($$"""[{"action":"commented","comment":"hi","user":{{Agent}},"createdOn":""}]""");

        Assert.Null(Waiting.Compute(Ticket("In Progress"), history, People(), resolved: false, complete: true, Now).IdleDays);
    }

    [Theory]
    [InlineData("Awaiting vendor support", "thirdParty")]
    [InlineData("Waiting on external engineer", "thirdParty")]
    [InlineData("Waiting on support", "assignee")]
    [InlineData("Pending agent", "assignee")]
    [InlineData("Waiting on customer", "requestor")]
    public void Third_party_terms_decide_before_help_desk_ones(string state, string expected) =>
        Assert.Equal(expected, Waiting.Compute(Ticket(state), [], People(), resolved: false, complete: true, Now).WaitingOn);

    [Fact]
    public void Unknown_resolution_is_said_as_the_reason_waiting_on_is_missing()
    {
        Ticket custom = Parse<Ticket>($$"""{"id":"t","status":"st_1","isCustomWorkflow":true,"requestor":{{Requestor}},"assignee":{{Agent}},"createdOn":"2026-11-10T09:00:00Z"}""");
        List<Activity> reply = Parse<List<Activity>>($$"""[{"action":"commented","comment":"hi","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""");

        Waiting.State state = Waiting.Compute(custom, reply, People(), resolved: null, complete: true, Now);

        Assert.Null(state.WaitingOn);
        Assert.True(state.ResolutionUnknown);
        Assert.False(Waiting.Compute(Ticket("In Progress"), [], People(), resolved: false, complete: false, Now).ResolutionUnknown);
    }

    [Fact]
    public void A_state_label_beats_a_transition_with_the_same_label()
    {
        Ticket t = Parse<Ticket>("""
            {"id":"t","status":"s_open","isCustomWorkflow":true,
             "workflow":[{"id":"s_open","label":"Open","nextSteps":[{"targetId":"s_closed"},{"targetId":"s_resolved","transitionLabel":"Closed"}]},
                         {"id":"s_closed","label":"Closed","nextSteps":[]},{"id":"s_resolved","label":"Resolved","nextSteps":[]}]}
            """);

        Assert.Equal("s_closed", WorkflowStatus.CheckTarget(t, "Closed", null));
        Assert.Equal("s_resolved", WorkflowStatus.CheckTarget(t, "s_resolved", null));
    }

    // ---- Review round 7 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_priority_only_preview_reads_the_description_so_an_edit_to_it_alone_is_caught()
    {
        string Row(string description) => $$"""{"id":"{{Id(1)}}","ticketNo":101,"title":"T1","status":"Open","priority":"Low","lastUpdatedOn":"2026-11-18T12:00:00Z","_ts":1,"description":"{{description}}"}""";
        var store = new BulkStore(Clock);
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : "{\"items\":[" + Row("before") + "],\"itemCount\":1}");
        using JsonDocument doc = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "T*" }, set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));
        var confirm = new Router(_ => "{\"item\":" + Row("after") + "}");   // same _ts and lastUpdatedOn

        using JsonDocument result = JsonDocument.Parse(await Bulk(confirm, store).BulkUpdate(doc.RootElement.GetProperty("confirmToken").GetString()!, Ct));

        Assert.Contains("description", TestFactory.Query(preview.Requests.Single(r => r.Path.EndsWith("/tickets", StringComparison.Ordinal)).Uri)["select"], StringComparison.Ordinal);
        Assert.Equal("changedSincePreview", result.RootElement.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain(confirm.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_ticket_changed_in_the_second_of_the_preview_waits_for_another_preview()
    {
        long justNow = Now.ToUnixTimeSeconds(), earlier = Now.AddMinutes(-1).ToUnixTimeSeconds();
        string tickets = "{\"items\":[" + Bulk(1, "T1", "In Progress", justNow) + "," + Bulk(2, "T2", "In Progress", earlier) + "],\"itemCount\":2}";
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : tickets);

        using JsonDocument doc = JsonDocument.Parse(await Bulk(preview, new BulkStore(Clock)).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "T*" }, set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        JsonElement[] items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains("just now", items.Single(i => i.GetProperty("ticketNo").GetInt32() == 101).GetProperty("blocked").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void A_full_store_refuses_a_preview_without_touching_anyone_elses_tokens()
    {
        var store = new BulkStore(Clock, maxPerOwner: 2, max: 3);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        BulkStore.Batch Batch(string owner) => new(owner, Now + BulkStore.Lifetime, "b", plan, []);
        List<string> theirs = [store.Add(Batch("other")), store.Add(Batch("other"))];
        string mine = store.Add(Batch("me"));

        Assert.Throws<McpException>(() => store.Add(Batch("me")));        // the store is full: refused, not made room in
        Assert.All(theirs, t => Assert.NotNull(store.Find(t)));
        Assert.NotNull(store.Find(mine));

        var own = new BulkStore(Clock, maxPerOwner: 2, max: 100);
        List<string> tokens = Enumerable.Range(0, 3).Select(_ => own.Add(Batch("me"))).ToList();
        Assert.Null(own.Find(tokens[0]));                                  // over their own cap: their oldest goes
    }

    [Fact]
    public void Confirmations_that_make_no_progress_stay_within_the_callers_places()
    {
        var store = new BulkStore(Clock, maxPerOwner: 3, max: 100);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        BulkStore.Batch Batch() => new("me", Now + BulkStore.Lifetime, "b", plan, []);
        BulkStore.Batch current = Batch();
        var tokens = new List<string> { store.Add(current) };

        for (int i = 0; i < 10; i++)
        {
            // Out of quota before the first ticket each time: the whole batch comes back as a resume token.
            Assert.True(current.TryStart());
            BulkStore.Batch rest = Batch();
            tokens.Add(store.Add(rest, resume: true));
            current.Finish(new JsonObject(), Now);
            current = rest;
        }

        Assert.True(tokens.Count(t => store.Find(t) is not null) <= 3);
        Assert.NotNull(store.Find(tokens[^1]));                             // the work still to do is always kept
    }

    [Fact]
    public async Task When_no_resume_token_can_be_kept_the_tickets_not_reached_are_named()
    {
        var store = new BulkStore(Clock, maxPerOwner: 20, max: 2);
        var caller = new KeyedCaller("me");
        string token = await PreviewWeeklyAsync(store, caller);
        BulkPlan plan = new(null, null, null, "Urgent", null, null, false, null);
        store.Add(new BulkStore.Batch("someone else", Now + BulkStore.Lifetime, "b", plan, []));   // now full

        var confirm = new Router(r => r.Method == HttpMethod.Get
            ? "{\"item\":" + Bulk(1, "Weekly check: backups", "In Progress", 1000) + "}"
            : "{\"item\":" + Bulk(1, "x", "Resolved", 9) + "}");
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 2));
        using var quota = new UpstreamQuota(opts);
        var client = new TicketingClient(new HttpClient(confirm), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, Clock),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, Clock, quota, caller);
        var bulk = new BulkTools(client, new FixedActor(Jane), new InstanceCache(opts, new TimeZoneOffsetResolver(opts, Clock), Clock), opts, store, caller);

        using JsonDocument doc = JsonDocument.Parse(await bulk.BulkUpdate(token, Ct));

        Assert.False(doc.RootElement.TryGetProperty("resumeToken", out _));
        Assert.Equal([102], doc.RootElement.GetProperty("notReached").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Contains("notReached", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"workflows":[{"id":"c_open","label":"Open","nextSteps":[]},{"id":"c_wait","label":"Waiting","nextSteps":[]},{"id":"c_wait2","label":"Waiting","nextSteps":[]},{"id":"c_vendor","label":"Vendor","nextSteps":[]}]}""")]
    [InlineData("""{"graphs":[{"nodes":[{"id":"c_open","type":"stateNode","data":{"label":"Open"}},{"id":"c_wait","type":"stateNode","data":{"label":"Waiting"}},{"id":"c_wait2","type":"stateNode","data":{"label":"Waiting"}},{"id":"c_vendor","type":"stateNode","data":{"label":"Vendor"}}],"edges":[]}]}""")]
    public void Every_state_can_be_chosen_by_one_rule_in_either_settings_shape(string settings)
    {
        JsonElement workflows = JsonDocument.Parse(settings).RootElement;

        foreach (WorkflowState s in WorkflowStatus.StatesIn(workflows))
        {
            Assert.Equal(s.Id, WorkflowStatus.ResolveFilter(s.Id!, workflows, "status"));   // every custom state by its ID
        }

        Assert.All(WorkflowStatus.DefaultStates, d => Assert.Equal(d, WorkflowStatus.ResolveFilter(d, workflows, "status")));
        Assert.Equal("c_vendor", WorkflowStatus.ResolveFilter("vendor", workflows, "status"));        // a label only it has
        McpException ex = Assert.Throws<McpException>(() => WorkflowStatus.ResolveFilter("Waiting", workflows, "status"));
        Assert.Contains("c_wait", ex.Message, StringComparison.Ordinal);
        Assert.Contains("c_wait2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Open (ID c_open)", WorkflowStatus.Compact(workflows)!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_ticket_is_resolved_when_resolved_status_names_its_states_label()
    {
        Ticket t = Parse<Ticket>("""
            {"id":"t","status":"c_done","isCustomWorkflow":true,"resolvedStatus":["Closed"],
             "workflow":[{"id":"c_done","label":"Closed","nextSteps":[]}]}
            """);

        Assert.True(TicketViews.Detail(t, includeHtml: false)["isResolved"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Here are the logs.\nOn startup the logger wrote:\nERROR: VPN configuration missing", false)]
    [InlineData("Thanks.\nOn Mon, 18 Nov 2026 at 10:00, Pat Example <pat@example.test> wrote:\nold text", true)]
    [InlineData("Thanks.\r\nOn 18 Nov 2026, at 10:00, Pat <pat@example.test> wrote:\r\n\r\nold text", true)]
    [InlineData("Thanks.\nOn Monday Pat wrote:\n\n> old text", true)]
    public void Only_a_wrote_line_with_email_evidence_cuts_the_text(string text, bool cut) =>
        Assert.Equal(cut, TicketViews.StripQuoted(text).Contains("[quoted reply cut", StringComparison.Ordinal));

    [Theory]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("#1234567890", 1234567890)]
    [InlineData("2147483648", null)]
    [InlineData("12345678901", null)]
    public void Ticket_numbers_take_up_to_ten_digits_within_range(string given, int? expected) =>
        Assert.Equal(expected, TicketRefs.BareNumber(given));

    // ---- Review round 8 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_confirmations_at_once_over_one_ticket_write_it_once()
    {
        var store = new BulkStore(Clock);
        string first = await PreviewWeeklyAsync(store);
        string second = await PreviewWeeklyAsync(store);
        int writes = 0;
        long ts1 = 1000;
        var confirm = new Router(r =>
        {
            if (r.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref writes);
                Thread.Sleep(150);                       // long enough for the other confirmation to re-read meanwhile
                Interlocked.Exchange(ref ts1, 1001);   // the write moves the ticket's _ts
                return "{\"item\":" + Bulk(1, "x", "Resolved", 1001) + "}";
            }

            return "{\"item\":" + (r.Path.EndsWith(Id(1), StringComparison.Ordinal)
                ? Bulk(1, "Weekly check: backups", "In Progress", Interlocked.Read(ref ts1))
                : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}";
        });
        BulkTools bulk = Bulk(confirm, store);

        string[] results = await Task.WhenAll(Task.Run(() => bulk.BulkUpdate(first, Ct)), Task.Run(() => bulk.BulkUpdate(second, Ct)));

        int applied101 = results.Sum(r => JsonDocument.Parse(r).RootElement.GetProperty("applied").EnumerateArray().Count(a => a.GetInt32() == 101));
        Assert.Equal(1, applied101);
        Assert.Contains(results, r => r.Contains("changedSincePreview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_confirmation_cancelled_before_any_write_puts_the_ticket_back_with_the_rest()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var confirm = new Router(_ =>
        {
            cts.Cancel();   // the client gives up during the first ticket's re-read
            throw new OperationCanceledException(cts.Token);
        });
        BulkTools bulk = Bulk(confirm, store);

        try
        {
            await bulk.BulkUpdate(token, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        using JsonDocument doc = JsonDocument.Parse(await bulk.BulkUpdate(token, Ct));

        Assert.False(doc.RootElement.TryGetProperty("failed", out _));      // nothing was written, so nothing to check
        Assert.Equal(2, store.Find(doc.RootElement.GetProperty("resumeToken").GetString()!)!.Items.Count);
    }

    [Fact]
    public async Task A_mistake_in_the_tags_given_fails_the_preview_once_and_a_comment_is_shown_word_for_word()
    {
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson
            : r.Path.EndsWith("/tags", StringComparison.Ordinal) ? """{"items":[{"id":"net","text":"Network","tags":[{"text":"VPN"}]}]}"""
            : WeeklyChecks());
        BulkTools bulk = Bulk(preview, new BulkStore(Clock));

        await Assert.ThrowsAsync<McpException>(() => bulk.PreviewBulkUpdate(filter: new BulkFilter { TitlePattern = "Weekly check:*" },
            set: new BulkChanges { RemoveTags = [new TagRemoval { Text = "VPN", TagCategoryId = "No such category" }] }, cancellationToken: Ct));
        using JsonDocument doc = JsonDocument.Parse(await bulk.PreviewBulkUpdate(filter: new BulkFilter { TitlePattern = "Weekly check:*" },
            set: Resolve, comment: new BulkComment { Text = "Checks complete.", IsPrivate = false }, cancellationToken: Ct));

        JsonElement comment = doc.RootElement.GetProperty("to").GetProperty("comment");
        Assert.Equal("Checks complete.", comment.GetProperty("text").GetString());
        Assert.Equal("reply the requestor sees", comment.GetProperty("visibility").GetString());
    }

    [Fact]
    public void A_transition_label_names_the_state_it_leads_to_only_when_no_state_has_that_name()
    {
        Ticket t = Parse<Ticket>("""
            {"id":"t","status":"s_closed","isCustomWorkflow":true,
             "workflow":[{"id":"s_open","label":"Open","nextSteps":[{"targetId":"s_closed","transitionLabel":"Close"},{"targetId":"s_resolved","transitionLabel":"Resolved"}]},
                         {"id":"s_closed","label":"Closed","nextSteps":[{"targetId":"s_open","transitionLabel":"Reopen"}]},
                         {"id":"s_resolved","label":"Resolved","nextSteps":[]}]}
            """);
        // Closed can't be reached from Open here; only a transition labelled "Closed", which leads to Resolved.
        Ticket open = Parse<Ticket>("""
            {"id":"t","status":"s_open","isCustomWorkflow":true,
             "workflow":[{"id":"s_open","label":"Open","nextSteps":[{"targetId":"s_resolved","transitionLabel":"Closed"}]},
                         {"id":"s_closed","label":"Closed","nextSteps":[]},{"id":"s_resolved","label":"Resolved","nextSteps":[]}]}
            """);

        Assert.True(WorkflowStatus.IsIn(t, "Close"));                                        // already where "Close" leads
        Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(open, "Closed", null));  // the state, refused; not Resolved
    }

    [Fact]
    public void A_requestor_reopening_without_a_word_puts_the_ticket_back_with_the_assignee()
    {
        List<Activity> history = Parse<List<Activity>>($$"""
            [{"action":"reopened","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"},
             {"action":"resolved","comment":"Fixed, closing.","user":{{Agent}},"createdOn":"2026-11-17T12:00:00Z"}]
            """);

        Waiting.State state = Waiting.Compute(Ticket("Reopened"), history, People(), resolved: false, complete: false, Now);

        Assert.Equal("assignee", state.WaitingOn);
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), state.WaitingSince);
    }

    [Fact]
    public void The_quoted_reply_note_comes_after_the_cut_whole()
    {
        string text = new string('n', 300) + "\r\nFrom: Pat\r\nSent: Monday\r\nTo: Help desk\r\nSubject: Re: help\r\n\r\n" + new string('q', 5000);

        string? reply = TicketViews.Reply("\r\n  " + text, 160);

        Assert.EndsWith("[quoted reply cut; list_ticket_activities with maxChars=20000 has it]", reply, StringComparison.Ordinal);
        Assert.Contains("[140 more characters]", reply, StringComparison.Ordinal);   // the new part's count, not the note's
    }

    [Fact]
    public void A_custom_state_shows_its_label_with_its_id_and_full_text_shows_whole_changes()
    {
        Ticket t = Parse<Ticket>("""
            {"id":"t","ticketNo":5,"status":"c_wait","isCustomWorkflow":true,"workflow":[{"id":"c_wait","label":"Waiting on vendor","nextSteps":[]}]}
            """);
        Activity a = Parse<Activity>($$"""{"id":"a","action":"updated","createdOn":"2026-11-18T12:00:00Z","changes":[{"name":"description","oldValue":"{{new string('o', 300)}}","newValue":"x"}]}""");

        JsonObject row = TicketViews.Row(t);
        Assert.Equal(("Waiting on vendor", "c_wait"), (row["status"]!.GetValue<string>(), row["statusId"]!.GetValue<string>()));
        Assert.Equal(300, TicketViews.Activity(a, includeHtml: false, TicketViews.FullTextChars)["changes"]![0]!["from"]!.GetValue<string>().Length);
        Assert.True(TicketViews.Activity(a, includeHtml: false)["changes"]![0]!["from"]!.GetValue<string>().Length < 300);
    }

    [Fact]
    public void A_custom_ticket_whose_state_label_cant_be_found_has_an_unknown_resolution()
    {
        Ticket t = Parse<Ticket>("""{"id":"t","status":"c_done","isCustomWorkflow":true,"resolvedStatus":["Closed"]}""");

        Assert.Null(WorkflowStatus.IsResolved(t));
        Assert.True(WorkflowStatus.IsResolved(Parse<Ticket>("""{"id":"t","status":"c_done","isCustomWorkflow":true,"resolvedStatus":["c_done"]}""")));
    }

    // ---- Review round 9 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancel_while_a_comment_waits_to_be_sent_puts_the_ticket_back()
    {
        // A comment, or a status change carrying one, is never cut off once sent, so a cancel ends it only before.
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var confirm = new Router(r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return "{\"item\":" + (r.Path.Contains(Id(1), StringComparison.Ordinal) ? Bulk(1, "Weekly check: backups", "In Progress", 1000) : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}";
        });
        BulkTools bulk = Bulk(confirm, store);

        try
        {
            await bulk.BulkUpdate(token, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        using JsonDocument doc = JsonDocument.Parse(await bulk.BulkUpdate(token, Ct));

        Assert.False(doc.RootElement.TryGetProperty("failed", out _));
        Assert.Equal(2, store.Find(doc.RootElement.GetProperty("resumeToken").GetString()!)!.Items.Count);
    }

    [Fact]
    public async Task A_queue_whose_instance_settings_fail_reads_them_once()
    {
        // The SLA setting, the staff list and the workflows all come from one read, and a failed read isn't cached.
        string tickets = Rows(3, _ => ",\"createdOn\":\"2026-11-01T00:00:00Z\"");
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => """{"error":true,"message":"Instance unavailable."}""",
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => tickets,
            _ => """{"items":[]}""",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);

        await new QueueTools(client, new FixedActor(Jane), cache, options, Clock).GetQueue("team", include: ["waitingOn", "idleDays", "sla"], cancellationToken: Ct);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ticket_gate_waits_only_as_long_as_it_is_given_and_frees_when_released()
    {
        var store = new BulkStore(Clock);
        Guid ticket = Guid.NewGuid();

        IDisposable? first = await store.EnterTicketAsync(ticket, TimeSpan.Zero, Ct);
        IDisposable? second = await store.EnterTicketAsync(ticket, TimeSpan.FromMilliseconds(50), Ct);
        IDisposable? other = await store.EnterTicketAsync(Guid.NewGuid(), TimeSpan.Zero, Ct);   // another ticket isn't held up
        first!.Dispose();
        IDisposable? third = await store.EnterTicketAsync(ticket, TimeSpan.Zero, Ct);

        Assert.Null(second);
        Assert.NotNull(other);
        Assert.NotNull(third);
        other!.Dispose();
        third!.Dispose();
    }

    [Fact]
    public async Task A_write_retried_after_a_5xx_then_held_back_for_rate_may_have_been_carried_out()
    {
        // The 503 may have come after the change was made; the retry is then refused by the caller's quota (one a minute).
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.ServiceUnavailable, "{}");
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1));
        using var quota = new UpstreamQuota(opts);
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, Clock),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, Clock, quota, new KeyedCaller("me"));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            client.UpdateTicketStatusAsync(Guid.NewGuid(), "Resolved", null, null, new TicketUser("u1", "Jane", "jane@example.test"), null, Ct));

        Assert.Single(handler.Requests);
        Assert.True(ex.MayHaveBeenCarriedOut);
        Assert.False(ex.OutcomeUnknown);  // a status change without a note is safe to repeat
        Assert.Contains("An earlier attempt may have been carried out", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRateLimited);   // so a bulk confirmation reports it rather than resume it as untouched
    }

    [Fact]
    public void A_transition_label_that_leads_on_from_here_isnt_where_the_ticket_already_is()
    {
        // Draft -Approve-> Manager approved -Approve-> Director approved: at the middle state, "Approve" still moves it.
        Ticket t = Parse<Ticket>("""
            {"id":"t","status":"s_mgr","isCustomWorkflow":true,
             "workflow":[{"id":"s_draft","label":"Draft","nextSteps":[{"targetId":"s_mgr","transitionLabel":"Approve"}]},
                         {"id":"s_mgr","label":"Manager approved","nextSteps":[{"targetId":"s_dir","transitionLabel":"Approve"}]},
                         {"id":"s_dir","label":"Director approved","nextSteps":[]}]}
            """);

        Assert.False(WorkflowStatus.IsIn(t, "Approve"));
        Assert.Equal("s_dir", WorkflowStatus.CheckTarget(t, "Approve", null));
    }

    [Fact]
    public void Tags_in_a_shape_other_than_a_list_are_refused_not_read_as_none()
    {
        JsonElement single = JsonDocument.Parse("""{"tagCategoryId":"net","text":"VPN"}""").RootElement;

        Assert.Throws<McpException>(() => TagEdits.Current(single, []));
        Assert.Empty(TagEdits.Current(JsonDocument.Parse("null").RootElement, []));
    }

    [Theory]
    [InlineData("Load test:\nFrom: 10.1.1.1\nSent: 500 requests\nAll were denied", false)]
    [InlineData("Thanks.\nFrom: Pat <pat@example.test>\nSent: Monday\nTo: Help\nSubject: Re: VPN\nold", true)]
    public void A_header_block_needs_a_subject_to_be_cut_as_a_quoted_email(string text, bool cut) =>
        Assert.Equal(cut, TicketViews.StripQuoted(text).Contains("[quoted reply cut", StringComparison.Ordinal));

    [Fact]
    public void Custom_field_text_is_clipped_like_any_other_unless_the_whole_text_is_asked_for()
    {
        Ticket t = Parse<Ticket>($$"""{"id":"t","ticketNo":5,"customFields":{"notes":"{{new string('n', 5000)}}","count":3,"list":["{{new string('l', 5000)}}"]} }""");

        JsonNode fields = TicketViews.Detail(t, includeHtml: false, textChars: 100)["customFields"]!;
        JsonNode whole = TicketViews.Detail(t, includeHtml: false, textChars: TicketViews.FullTextChars)["customFields"]!;

        Assert.True(fields["notes"]!.GetValue<string>().Length < 200);
        Assert.True(fields["list"]![0]!.GetValue<string>().Length < 200);
        Assert.Equal(3, fields["count"]!.GetValue<int>());
        Assert.Equal(5000, whole["notes"]!.GetValue<string>().Length);
    }

    [Fact]
    public void Without_the_staff_list_a_reply_from_someone_unplaced_leaves_waiting_on_unknown()
    {
        string stranger = """{"id":"x9","name":"Sam","email":"sam@example.test"}""";
        List<Activity> history = Parse<List<Activity>>($$"""[{"action":"commented","comment":"Looking into it.","user":{{stranger}},"createdOn":"2026-11-18T12:00:00Z"}]""");

        Waiting.State none = Waiting.Compute(Ticket("In Progress"), history, TicketPeople.From(null, TestFactory.Options()), resolved: false, complete: true, Now);
        Waiting.State known = Waiting.Compute(Ticket("In Progress"), history, People(), resolved: false, complete: true, Now);

        Assert.Null(none.WaitingOn);
        Assert.True(none.AuthorUnknown);
        // The list can leave staff out, so reading it doesn't place Sam either: a cc, or staff it omits.
        Assert.Null(known.WaitingOn);
        Assert.True(known.AuthorUnknown);
    }

    [Theory]
    [InlineData("Pending approval", "assignee")]   // names no one: the requestor's reply decides
    [InlineData("Pending", "assignee")]
    [InlineData("Pending customer", "requestor")]
    [InlineData("Waiting on agent", "assignee")]
    [InlineData("Awaiting vendor", "thirdParty")]
    public void A_waiting_state_decides_only_when_it_names_whom(string status, string expected)
    {
        List<Activity> history = Parse<List<Activity>>($$"""[{"action":"commented","comment":"Any news?","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""");

        Assert.Equal(expected, Waiting.Compute(Ticket(status), history, People(), resolved: false, complete: true, Now, status).WaitingOn);
    }

    [Fact]
    public void A_tag_object_without_a_category_is_matched_by_its_text_and_a_malformed_category_is_refused()
    {
        List<TagCategory> categories = Parse<ListResponse<TagCategory>>("""{"items":[{"id":"net","text":"Network","tags":[{"text":"VPN"}]}]}""").Items!.ToList();
        JsonElement plain = JsonDocument.Parse("""[{"text":"VPN"},{"text":"VPN","tagCategoryId":null},{"text":"VPN","tagCategoryId":" "}]""").RootElement;
        JsonElement malformed = JsonDocument.Parse("""[{"text":"VPN","tagCategoryId":5}]""").RootElement;

        Assert.All(TagEdits.Current(plain, categories), t => Assert.Equal(new TicketTag("net", "VPN"), t));
        Assert.Throws<McpException>(() => TagEdits.Current(malformed, categories));
    }

    [Fact]
    public async Task Resolved_tickets_named_are_found_in_the_same_one_read()
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson
            : "{\"items\":[" + Bulk(1, "Old one", "Resolved", 1000) + "," + Bulk(2, "Older one", "Resolved", 2000) + "," + Bulk(3, "Open one", "Open", 3000) + "],\"itemCount\":3}");
        BulkTools bulk = Bulk(handler, new BulkStore(Clock));

        using JsonDocument doc = JsonDocument.Parse(await bulk.PreviewBulkUpdate(tickets: ["101", "102", "103"], set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        CapturedRoute list = Assert.Single(handler.Requests, r => r.Path.Contains("/tickets", StringComparison.Ordinal));   // no lookup, no single read
        Assert.DoesNotContain("isResolved", list.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_ticket_named_that_a_complete_read_lacks_is_reported_without_lookups()
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        BulkTools bulk = Bulk(handler, new BulkStore(Clock));

        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            bulk.PreviewBulkUpdate(tickets: ["101", "#999", "102"], set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        Assert.Contains("'#999'", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests, r => r.Path.Contains("/tickets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tickets_a_partial_read_lacks_are_read_within_one_budget_for_the_batch()
    {
        // The scan may read 100 tickets but the API says there are 500, so the five named aren't among those read; one
        // budget of 100 rows covers two single reads, and the third is refused rather than spend the caller's quota.
        var handler = new Router(r => r.Path switch
        {
            var p when p.EndsWith("/instance", StringComparison.Ordinal) => InstanceJson,
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => WeeklyChecks().Replace("\"itemCount\":4", "\"itemCount\":500", StringComparison.Ordinal),
            var p => "{\"item\":" + Bulk(int.Parse(p[^2..], CultureInfo.InvariantCulture), "Old", "Open", 1) + "}",
        });
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler, o => o.MaxScanTickets = 100);
        var bulk = new BulkTools(client, new FixedActor(Jane), cache, options, new BulkStore(Clock));

        McpException ex = await Assert.ThrowsAsync<McpException>(() => bulk.PreviewBulkUpdate(
            tickets: [Id(10), Id(11), Id(12), Id(13), Id(14)], set: new BulkChanges { Priority = "Urgent" }, cancellationToken: Ct));

        Assert.Contains("Name fewer tickets at a time", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count(r => r.Path.Contains("/tickets/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Count_groups_and_queue_judge_staff_from_every_assignee_seen()
    {
        var cache = new InstanceCache(Microsoft.Extensions.Options.Options.Create(TestFactory.Options()), new TimeZoneOffsetResolver(Microsoft.Extensions.Options.Options.Create(TestFactory.Options()), Clock), Clock);
        var alex = new TicketUser(Guid.NewGuid().ToString(), "Alex", "alex@example.test");

        cache.Assignees.Remember(alex);
        cache.Assignees.Remember(alex with { Name = "Alex B." });   // the same person, by object ID

        Assert.Single(cache.Assignees.All);
        Assert.True(TicketPeople.From(null, TestFactory.Options()).WithAgents(cache.Assignees.All).IsAgent(alex));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static async Task<string> PreviewWeeklyAsync(BulkStore store, IUpstreamCaller? caller = null)
    {
        var handler = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : WeeklyChecks());
        using JsonDocument doc = JsonDocument.Parse(await Bulk(handler, store, caller: caller).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Weekly check:*" }, set: Resolve,
            comment: new BulkComment { Text = "Checks complete.", IsPrivate = false }, timezoneOffset: 2, cancellationToken: Ct));
        return doc.RootElement.GetProperty("confirmToken").GetString()!;
    }

    private static BulkTools Bulk(Router handler, BulkStore store, ActingUser? actor = null, IUpstreamCaller? caller = null)
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = BuildRouted(handler);
        return new BulkTools(client, new FixedActor(actor ?? Jane), cache, options, store, caller);
    }

    private static (TicketingClient, InstanceCache, IOptions<TicketingOptions>) BuildRouted(HttpMessageHandler handler, Action<TicketingOptions>? configure = null)
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(configure));
        var tz = new TimeZoneOffsetResolver(opts, Clock);
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), tz,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, Clock);
        return (client, new InstanceCache(opts, tz, Clock), opts);
    }

    private static string Id(int n) => $"00000000-0000-0000-0000-{n:D12}";

    private static Ticket Ticket(string status) =>
        Parse<Ticket>($$"""{"id":"t","status":"{{status}}","requestor":{{Requestor}},"assignee":{{Agent}},"createdOn":"2026-11-10T09:00:00Z"}""");

    private static TicketPeople People() => TicketPeople.From(ParseInstance(), TestFactory.Options());

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, TicketingClient.JsonOptions)!;

    private sealed record CapturedRoute(HttpMethod Method, string Path, Uri Uri, string? Body);

    /// <summary>Answers by request, for tools that make requests in parallel or in an order a test shouldn't fix.</summary>
    private sealed class Router(Func<CapturedRoute, string> respond) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<CapturedRoute> _requests = [];

        public IReadOnlyList<CapturedRoute> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRoute(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri, body);
            lock (_lock)
            {
                _requests.Add(captured);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, respond(captured));
        }
    }

    private sealed class KeyedCaller(string key) : IUpstreamCaller
    {
        public string? Key => key;
    }

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
