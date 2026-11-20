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

    public static TheoryData<string, string, string?> WaitingCases => new()
    {
        // The latest public reply by a person decides.
        { $$"""[{"action":"commented","comment":"Still broken","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-17T12:00:00Z"}]""", "requestor", "2026-11-17T12:00:00Z" },
        // Someone else on the requestor's side (a cc) counts as the requestor.
        { $$"""[{"action":"commented","comment":"Me too","user":{{Cc}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        // Private notes, automation, and the help desk's mailbox aren't replies.
        { $$"""[{"action":"commented","comment":"note","isPrivate":true,"user":{{Agent}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"commented","comment":"We got your email","user":{{Mailbox}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Help","user":{{Requestor}},"createdOn":"2026-11-18T12:00:00Z"}]""", "assignee", "2026-11-18T12:00:00Z" },
        { $$"""[{"action":"automation_reply","comment":"Auto","automation":{"ruleId":"x"},"user":{{Rule}},"createdOn":"2026-11-19T10:00:00Z"}, {"action":"commented","comment":"Try this","user":{{Agent}},"createdOn":"2026-11-18T12:00:00Z"}]""", "requestor", "2026-11-18T12:00:00Z" },
        // Nobody has replied: the assignee, since the ticket was created.
        { $$"""[{"action":"created","user":{{Requestor}},"createdOn":"2026-11-10T09:00:00Z"}]""", "assignee", "2026-11-10T09:00:00Z" },
    };

    [Theory]
    [MemberData(nameof(WaitingCases))]
    public void Waiting_on_follows_the_latest_public_reply_by_a_person(string history, string waitingOn, string? since)
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

        Assert.Equal("assignee", Waiting.Compute(Ticket("In Progress"), activities, People(), resolved: false, complete: true, Now).WaitingOn);
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
            {"id":"act-{{i}}-0000000000000000","action":"commented","comment":"{{new string('w', 300)}}\n\nFrom: Someone\nSent: earlier\n{{new string('q', 2000)}}",
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
    [InlineData("Thanks.\n________________________________\nFrom: Pat <pat@example.test>\nSent: Monday\nTo: Help\nold")]
    [InlineData("Thanks.\nFrom: Pat <pat@example.test>\nDate: Monday\nold")]
    [InlineData("Thanks.\n-----Original Message-----\nold")]
    [InlineData("Thanks.\nOn Mon, 1 Jan 2026 at 10:00, Pat wrote:\n> old")]
    [InlineData("Thanks.\r\n-----Original Message-----\r\nold")]
    [InlineData("Thanks.\r\nOn Mon, 1 Jan 2026 at 10:00, Pat wrote:\r\n> old")]
    [InlineData("Thanks.\r\nFrom: Pat <pat@example.test>\r\nSent: Monday\r\nold")]
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

        Assert.True(TicketVersion.Of(listed).Matches(TicketVersion.Of(read)));        // only one has _ts: the fields decide
        Assert.False(TicketVersion.Of(read).Matches(TicketVersion.Of(sameSecond)));   // changed in the same second: _ts alone would miss it
        Assert.False(TicketVersion.Of(read).Matches(TicketVersion.Of(commented)));    // a comment moves _ts only
    }

    [Fact]
    public async Task A_custom_workflow_status_change_sends_the_state_id_read_from_whole_tickets()
    {
        // isCustomWorkflow can't be selected, so a preview that changes status reads whole tickets to learn it.
        string ticket = $$"""
            {"id":"{{Id(1)}}","ticketNo":101,"title":"Custom","status":"st_7","isCustomWorkflow":true,"_ts":1,
             "workflow":[{"id":"st_7","label":"Working","nextSteps":[{"targetId":"st_8"}]},{"id":"st_8","label":"Done","nextSteps":[]}]}
            """;
        var store = new BulkStore(Clock);
        var preview = new Router(r => r.Path.EndsWith("/instance", StringComparison.Ordinal) ? InstanceJson : "{\"items\":[" + ticket + "],\"itemCount\":1}");
        using JsonDocument doc = JsonDocument.Parse(await Bulk(preview, store).PreviewBulkUpdate(
            filter: new BulkFilter { TitlePattern = "Custom" }, set: new BulkChanges { Status = "Done" }, cancellationToken: Ct));
        var confirm = new Router(r => "{\"item\":" + ticket + "}");

        await Bulk(confirm, store).BulkUpdate(doc.RootElement.GetProperty("confirmToken").GetString()!, Ct);

        Assert.False(TestFactory.Query(preview.Requests.Single(r => r.Path.EndsWith("/tickets", StringComparison.Ordinal)).Uri).ContainsKey("select"));
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
        Assert.True(Of("""["VPN"]""").Matches(Of("""[{"tagCategoryId":"net","text":"VPN"}]""")));   // one read without categories: texts decide
    }

    [Fact]
    public async Task An_interrupted_confirmation_keeps_what_it_did_for_the_retry()
    {
        var store = new BulkStore(Clock);
        string token = await PreviewWeeklyAsync(store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var confirm = new Router(r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                cts.Cancel();   // the client gives up while the first ticket's change is out
                throw new OperationCanceledException(cts.Token);
            }

            return "{\"item\":" + (r.Path.EndsWith(Id(1), StringComparison.Ordinal) ? Bulk(1, "Weekly check: backups", "In Progress", 1000) : Bulk(2, "Weekly check: patching", "In Progress", 2000)) + "}";
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

        Assert.Contains("Interrupted", Assert.Single(doc.RootElement.GetProperty("failed").EnumerateArray()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.NotNull(store.Find(doc.RootElement.GetProperty("resumeToken").GetString()!));   // ticket 102, not reached
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
            var p when p.EndsWith("/tickets", StringComparison.Ordinal) => Rows(1, _ => ",\"createdOn\":\"2026-11-10T00:00:00Z\""),
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
