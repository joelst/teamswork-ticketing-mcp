using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Changes that help an agent get a call right the first time.</summary>
public sealed class UsabilityTests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The ticket number is ticketNo, and ticketId is the UUID ---------------------------------------------------

    [Fact]
    public async Task A_selected_ticket_number_comes_back_as_ticketNo()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":1041,"title":"VPN"}]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options)
            .ListTickets(select: "id,ticketNo,title", orderBy: "ticketNo", cancellationToken: Ct));

        JsonElement row = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(1041, row.GetProperty("ticketNo").GetInt32());
        Assert.False(row.TryGetProperty("ticketId", out _));
        Dictionary<string, string> q = TestFactory.Query(handler.Requests[0].Uri);
        Assert.StartsWith("id,ticketId,title,", q["select"], StringComparison.Ordinal); // the row's fields, by the API's name for the number
        Assert.Equal("ticketId", q["orderBy"]);
    }

    [Theory]
    [InlineData("ticketId,ticketNo", "ticketId")] // the API's own name still works, and isn't sent twice
    [InlineData("title", "title")]
    public void Select_accepts_both_names_for_the_ticket_number(string select, string sent)
    {
        Assert.Equal(sent, ToolValidation.OptionalSelect(select));
    }

    [Theory]
    [InlineData("ticketNo")]
    [InlineData("ticketId")]
    [InlineData("TICKETNO")]
    public void Sorting_by_ticket_number_sends_the_api_field(string orderBy)
    {
        Assert.Equal("ticketId", ToolValidation.OptionalOrderBy(orderBy));
    }


    // ---- 'status' on list_tickets is one exact state ---------------------------------------------------------------

    // The instance's workflow settings aren't documented; states are found by their shape (id and nextSteps).
    private static readonly JsonElement CustomWorkflows = JsonDocument.Parse("""
        [{"id":"w1","name":"Support","states":[
          {"id":"s1","label":"Waiting on vendor","nextSteps":[]},
          {"id":"s2","label":"Open","nextSteps":[]}]}]
        """).RootElement;

    [Theory]
    [InlineData("in progress", "In Progress")] // a default state, in its canonical form
    [InlineData("waiting on VENDOR", "s1")]    // a custom label becomes its ID
    [InlineData("S1", "s1")]
    public void A_state_is_sent_as_the_id_the_api_matches(string given, string sent)
    {
        Assert.Equal(sent, WorkflowStatus.ResolveFilter(given, CustomWorkflows, "status"));
    }

    [Fact]
    public void A_default_name_a_custom_state_shares_picks_the_default_state_and_the_custom_one_goes_by_its_id()
    {
        // A default state's name is its ID, so it decides; the custom state with that label stays reachable by its own.
        Assert.Equal("Open", WorkflowStatus.ResolveFilter("open", CustomWorkflows, "status"));
        Assert.Equal("s2", WorkflowStatus.ResolveFilter("s2", CustomWorkflows, "status"));
    }

    [Fact]
    public void An_unknown_state_is_refused_when_the_states_are_known()
    {
        McpException ex = Assert.Throws<McpException>(() => WorkflowStatus.ResolveFilter("Pending", CustomWorkflows, "status"));

        Assert.Contains("Waiting on vendor (s1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("isResolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_is_sent_as_given_when_the_instance_names_no_custom_states()
    {
        Assert.Equal("custom-state-7", WorkflowStatus.ResolveFilter("custom-state-7", null, "status"));
        Assert.Equal("custom-state-7", WorkflowStatus.ResolveFilter("custom-state-7", JsonDocument.Parse("""[{"id":"w1"}]""").RootElement, "status"));
    }

    // The shape the live API returns: each workflow as a graph editor's nodes and edges. The default workflow's node IDs
    // are its labels; a custom one's here are GUIDs. The start node has a blank label and isn't a state.
    private static readonly JsonElement GraphWorkflows = JsonDocument.Parse("""
        {"ticket":[
          {"nodes":[
            {"id":"Ticket Creation","type":"startNode","data":{"label":""}},
            {"id":"Open","type":"startNode","data":{"label":"Open"}},
            {"id":"In Progress","type":"textFieldNode","data":{"label":"In Progress"}}],
           "edges":[{"id":"e1","type":"labeledEdge","label":"Start Ticket","source":"Open","target":"In Progress","data":{"recordComment":false}}]},
          {"nodes":[
            {"id":"7d0c2a8e-0000-4000-8000-000000000001","type":"textFieldNode","data":{"label":"Waiting on vendor"}}],
           "edges":[]}],
         "isCustomWorkflow":false}
        """).RootElement;

    [Theory]
    [InlineData("open", "Open")]                                                    // a default state's node ID is its label
    [InlineData("In progress", "In Progress")]
    [InlineData("waiting on vendor", "7d0c2a8e-0000-4000-8000-000000000001")]       // a custom node's label becomes its ID
    public void States_are_read_from_the_live_graph_shape(string given, string sent)
    {
        Assert.Equal(sent, WorkflowStatus.ResolveFilter(given, GraphWorkflows, "status"));
    }

    [Fact]
    public void The_graph_start_node_and_edges_are_not_states()
    {
        Assert.DoesNotContain(WorkflowStatus.StatesIn(GraphWorkflows), s => s.Id is "Ticket Creation" or "e1");
        Assert.Throws<McpException>(() => WorkflowStatus.ResolveFilter("Start Ticket", GraphWorkflows, "status"));
    }

    [Fact]
    public async Task List_tickets_sends_status_as_the_exact_state_filter()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","workflows":[{"id":"w1"}]}}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options).ListTickets(status: "in progress", cancellationToken: Ct);

        Dictionary<string, string> q = TestFactory.Query(handler.Requests[1].Uri);
        Assert.Equal("In Progress", q["statusId"]);
        Assert.False(q.ContainsKey("status")); // the API's coarse resolved/unresolved filter isn't used
    }

    [Fact]
    public async Task A_rows_status_label_and_id_together_name_one_state()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","workflows":[{"id":"w1","states":[{"id":"s1","label":"Waiting on vendor","nextSteps":[]}]}]}}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options).ListTickets(status: "Waiting on vendor", statusId: "s1", cancellationToken: Ct);

        Assert.Equal("s1", TestFactory.Query(handler.Requests[^1].Uri)["statusId"]);
    }

    [Fact]
    public void State_names_give_the_id_of_a_state_whose_label_another_shares()
    {
        List<string> names = WorkflowStatus.StateNames(CustomWorkflows);

        Assert.Contains("Open (ID s2)", names);        // a custom state named like the default Open
        Assert.Contains("Waiting on vendor", names);   // a label only one state has
    }

    [Fact]
    public async Task Status_and_its_older_name_must_agree()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(new FakeHttpHandler());

        await Assert.ThrowsAsync<McpException>(() =>
            new TicketTools(client, new FixedActor(Jane), cache, options).ListTickets(status: "Open", statusId: "Closed", cancellationToken: Ct));
    }

    // ---- Where a ticket can go next ---------------------------------------------------------------------------------

    private static string DefaultWorkflowTicket(string status) => $$$"""
        {"item":{"id":"{{{TicketA}}}","ticketNo":5,"status":"{{{status}}}","isCustomWorkflow":false,"resolvedStatus":["Resolved","Closed"],
          "workflow":[
            {"id":"Open","label":"Open","nextSteps":[
              {"targetId":"In Progress","transitionLabel":"Start work","authorizedUsers":["owner","assignee"]},
              {"targetId":"Resolved","transitionLabel":"Resolve","recordComment":true}]},
            {"id":"In Progress","label":"In Progress","nextSteps":[{"targetId":"Resolved","transitionLabel":"Resolve"}]},
            {"id":"Resolved","label":"Resolved","nextSteps":[]}]}}
        """;

    private const string CustomWorkflowTicket = $$$"""
        {"item":{"id":"{{{TicketA}}}","status":"s1","isCustomWorkflow":true,
          "workflow":[
            {"id":"s1","label":"New","nextSteps":[{"targetId":"s2","transitionLabel":"Pick up"}]},
            {"id":"s2","label":"Doing","nextSteps":[]}]}}
        """;

    private static Ticketing.Models.Ticket TicketFrom(string json) =>
        JsonSerializer.Deserialize<Ticketing.Models.ItemResponse<Ticketing.Models.Ticket>>(json, TicketingClient.JsonOptions)!.Item!;

    [Fact]
    public async Task Get_ticket_lists_the_allowed_next_statuses()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, DefaultWorkflowTicket("Open"));
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options).GetTicketBrief(TicketA, activities: 0, cancellationToken: Ct));

        JsonElement next = doc.RootElement.GetProperty("allowedNextStatuses");
        Assert.Equal(2, next.GetArrayLength());
        Assert.Equal("In Progress", next[0].GetProperty("status").GetString());
        Assert.Equal("Start work", next[0].GetProperty("transition").GetString());
        Assert.False(next[0].GetProperty("resolves").GetBoolean());
        Assert.Equal(["owner", "assignee"], next[0].GetProperty("allowedRoles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(next[1].GetProperty("requiresComment").GetBoolean());
        Assert.True(next[1].GetProperty("resolves").GetBoolean());
        Assert.Equal(5, doc.RootElement.GetProperty("ticketNo").GetInt32()); // the ticket itself is unchanged
    }

    [Fact]
    public void A_custom_workflow_offers_state_ids_and_takes_a_label()
    {
        Ticketing.Models.Ticket ticket = TicketFrom(CustomWorkflowTicket);

        Assert.Equal("s2", Assert.Single(WorkflowStatus.AllowedNext(ticket)!).Status);
        Assert.Equal("s2", WorkflowStatus.CheckTarget(ticket, "doing", null));    // the state's label
        Assert.Equal("s2", WorkflowStatus.CheckTarget(ticket, "Pick up", null));  // the transition's label
    }

    [Fact]
    public void A_step_the_workflow_does_not_allow_is_refused_with_the_allowed_ones()
    {
        McpException ex = Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(TicketFrom(DefaultWorkflowTicket("In Progress")), "Open", null));

        Assert.Contains("from 'In Progress'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Resolved via 'Resolve'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_needs_a_comment_is_refused_without_one()
    {
        Ticketing.Models.Ticket ticket = TicketFrom(DefaultWorkflowTicket("Open"));

        Assert.Contains("requires a comment", Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(ticket, "resolved", null)).Message, StringComparison.Ordinal);
        Assert.Equal("Resolved", WorkflowStatus.CheckTarget(ticket, "resolved", "Fixed the switch"));
    }

    [Theory]
    [InlineData("Unknown")] // a status no state has: nothing to check against
    [InlineData("Resolved")] // a state with no steps listed
    public void A_workflow_that_cant_be_read_doesnt_block_the_change(string status)
    {
        Assert.Equal("Anything", WorkflowStatus.CheckTarget(TicketFrom(DefaultWorkflowTicket(status)), "Anything", null));
        Assert.Null(WorkflowStatus.AllowedNext(TicketFrom(DefaultWorkflowTicket("Unknown"))));
    }

    [Fact]
    public async Task Update_ticket_status_sends_the_state_id_the_workflow_names()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, CustomWorkflowTicket)
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","status":"s2"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options).UpdateTicketStatus(TicketA, "Doing", cancellationToken: Ct);

        using JsonDocument body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("s2", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Update_ticket_status_goes_ahead_when_the_ticket_cant_be_read_first()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"Something odd"}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","status":"Doing"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options).UpdateTicketStatus(TicketA, "Doing", cancellationToken: Ct);

        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        using JsonDocument body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("Doing", body.RootElement.GetProperty("status").GetString());
    }

    // ---- Whose tickets, and their SLA flags, in one call --------------------------------------------------------------

    private const string PeopleTickets = """
        {"items":[
          {"id":"a","title":"Johns","assignee":{"id":"u2","name":"John Smith","email":"john@example.test"},"isRtBreached":true,"isFrtBreached":false},
          {"id":"b","title":"Janes","assignee":{"id":"u1","name":"Jane Doe","email":"jane@example.test"},
           "requestor":{"id":"pat@outside.test","name":"pat@outside.test","email":"pat@outside.test"}},
          {"id":"c","title":"Nobody's"},
          {"id":"d","title":"Blank","assignee":{"id":"","name":"","email":""}}],
         "itemCount":4}
        """;

    [Fact]
    public async Task A_queue_can_carry_sla_state()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, SlaOnInstanceJson)
            .Enqueue(HttpStatusCode.OK, PeopleTickets);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options)
            .GetQueue(who: "team", include: ["sla"], cancellationToken: Ct));

        Dictionary<string, string> q = TestFactory.Query(handler.Requests[1].Uri);
        Assert.False(q.ContainsKey("select")); // whole tickets, since two of the flags can't be selected
        Assert.Equal("200", q["limit"]);
        JsonElement first = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(("a", "rtBreached"), (first.GetProperty("id").GetString(), first.GetProperty("sla").GetString())); // breached goes first
        Assert.False(doc.RootElement.TryGetProperty("slaTracking", out _));
    }

    [Fact]
    public async Task Sla_tracking_off_is_said_once_and_tickets_are_selected()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","sla":{"frt":{"low":{"enabled":false}},"rt":{"low":{"enabled":false}}}}}""")
            .Enqueue(HttpStatusCode.OK, PeopleTickets);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options)
            .GetQueue(include: ["sla"], cancellationToken: Ct));

        Assert.Equal("off", doc.RootElement.GetProperty("slaTracking").GetString());
        Assert.True(TestFactory.Query(handler.Requests[1].Uri).ContainsKey("select"));
        Assert.All(doc.RootElement.GetProperty("items").EnumerateArray(), i => Assert.False(i.TryGetProperty("sla", out _)));
    }

    [Fact]
    public async Task Someone_elses_queue_is_found_by_name()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, PeopleTickets);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options)
            .GetQueue("John Smith", include: [], cancellationToken: Ct));

        JsonElement item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("a", item.GetProperty("id").GetString());
        Assert.False(item.TryGetProperty("assignee", out _)); // always them, so left out
        Assert.Equal("John Smith <john@example.test>", doc.RootElement.GetProperty("who").GetString());
    }

    [Fact]
    public async Task An_outside_requestor_is_found_by_email()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson) // just read, so not read again for the miss (a refresh waits 30 seconds)
            .Enqueue(HttpStatusCode.OK, PeopleTickets);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options)
            .GetQueue("PAT@outside.test", role: "requestor", include: [], cancellationToken: Ct));

        Assert.Equal("b", Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Unassigned_tickets_match_the_count_tickets_rule()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, PeopleTickets);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new QueueTools(client, new FixedActor(Jane), cache, options)
            .GetQueue("Unassigned", include: [], sort: "oldest", cancellationToken: Ct));

        Assert.Equal(["c", "d"], doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order());
    }

    [Fact]
    public async Task Unassigned_takes_the_assignee_role_only()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(new FakeHttpHandler());

        await Assert.ThrowsAsync<McpException>(() =>
            new QueueTools(client, new FixedActor(Jane), cache, options).GetQueue("unassigned", role: "requestor", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_name_that_matches_two_people_is_refused_with_both()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, InstanceJson);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            new QueueTools(client, new FixedActor(Jane), cache, options).GetQueue("John", include: [], cancellationToken: Ct));

        Assert.Contains("John Smith", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Johnny Appleseed", ex.Message, StringComparison.Ordinal);
    }

    // ---- Adding and removing tags, keeping the rest ----------------------------------------------------------------

    private const string Categories = """
        {"items":[
          {"id":"cat1","text":"Area","tags":[{"text":"Network"},{"text":"Old","deleted":true}]},
          {"id":"cat2","text":"Kind","tags":[{"text":"Hardware"},{"text":"Shared"}]},
          {"id":"cat3","text":"Other","tags":[{"text":"Shared"}]}]}
        """;

    private static string TicketWithTags(params string[] tags) =>
        $$$"""{"item":{"id":"{{{TicketA}}}","tags":{{{JsonSerializer.Serialize(tags)}}}}}""";

    private static List<(string Category, string Text)> SentTags(CapturedRequest put)
    {
        using JsonDocument body = JsonDocument.Parse(put.Body!);
        return body.RootElement.GetProperty("ticket").GetProperty("tags").EnumerateArray()
            .Select(t => (t.GetProperty("tagCategoryId").GetString()!, t.GetProperty("text").GetString()!)).ToList();
    }

    [Fact]
    public async Task Adding_a_tag_keeps_the_others_even_one_deleted_from_its_category()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, Categories)
            .Enqueue(HttpStatusCode.OK, TicketWithTags("Network", "Old"))
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options)
            .UpdateTicket(TicketA, addTags: [new TagRef { Text = "hardware" }], cancellationToken: Ct); // its one category is found

        Assert.Equal([("cat1", "Network"), ("cat1", "Old"), ("cat2", "Hardware")], SentTags(handler.Requests[2]));
    }

    [Fact]
    public async Task Removing_a_tag_keeps_the_others_and_reports_one_the_ticket_lacked()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, TicketWithTags("Network", "Hardware"))
            .Enqueue(HttpStatusCode.OK, Categories)
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options)
            .UpdateTicket(TicketA, removeTags: [new TagRef { Text = "NETWORK" }, new TagRef { Text = "Missing" }], cancellationToken: Ct));

        Assert.Equal([("cat2", "Hardware")], SentTags(handler.Requests[2]));
        Assert.Contains("'Missing'", doc.RootElement.GetProperty("warning").GetString(), StringComparison.Ordinal);
        Assert.Equal("""["Hardware"]""", doc.RootElement.GetProperty("changed").GetProperty("tags").GetRawText());
    }

    [Fact]
    public void Tags_written_show_the_category_only_for_a_text_in_two_categories()
    {
        JsonArray shown = TicketViews.TagList([new TicketTag("cat2", "Shared"), new TicketTag("cat3", "shared"), new TicketTag("cat1", "Network")])!;

        Assert.Equal("""[{"text":"Shared","tagCategoryId":"cat2"},{"text":"shared","tagCategoryId":"cat3"},"Network"]""", shown.ToJsonString());
    }

    [Fact]
    public async Task A_removal_naming_a_category_renamed_since_the_cache_was_filled_reads_the_categories_again()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, Categories)                                    // cached while "Area"
            .Enqueue(HttpStatusCode.OK, TicketWithTags("Network", "Hardware"))
            .Enqueue(HttpStatusCode.OK, Categories.Replace("\"Area\"", "\"Location\"", StringComparison.Ordinal))
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        var time = new MutableTime(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler, time: time);
        await cache.GetTagCategoriesAsync(client, refresh: false, Ct);
        time.Advance(InstanceCache.MinRefreshInterval + TimeSpan.FromSeconds(1));

        await new TicketTools(client, new FixedActor(Jane), cache, options)
            .UpdateTicket(TicketA, removeTags: [new TagRef { Text = "Network", TagCategoryId = "Location" }], cancellationToken: Ct);

        Assert.Equal([("cat2", "Hardware")], SentTags(handler.Requests[3]));
    }

    [Fact]
    public async Task A_ticket_tag_in_two_categories_is_refused_rather_than_guessed()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, TicketWithTags("Shared"))
            .Enqueue(HttpStatusCode.OK, Categories);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => new TicketTools(client, new FixedActor(Jane), cache, options)
            .UpdateTicket(TicketA, removeTags: [new TagRef { Text = "Hardware" }], cancellationToken: Ct));

        Assert.Contains("Kind, Other", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task The_whole_list_and_changes_to_it_cant_be_combined()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(new FakeHttpHandler());

        await Assert.ThrowsAsync<McpException>(() => new TicketTools(client, new FixedActor(Jane), cache, options).UpdateTicket(
            TicketA, tags: [new TagRef { TagCategoryId = "Area", Text = "Network" }], addTags: [new TagRef { Text = "Hardware" }], cancellationToken: Ct));
    }

    [Fact]
    public void A_tag_both_added_and_removed_is_refused()
    {
        Ticketing.Models.TicketTag hardware = new("cat2", "Hardware");

        Assert.Throws<McpException>(() => TagEdits.Apply([], [hardware], [new TagRef { Text = "hardware" }], []));
    }

    [Fact]
    public void A_tag_text_in_two_categories_needs_its_category()
    {
        IReadOnlyList<Ticketing.Models.TagCategory> categories =
            JsonSerializer.Deserialize<Ticketing.Models.ListResponse<Ticketing.Models.TagCategory>>(Categories, TicketingClient.JsonOptions)!.Items!;

        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.MatchTag(new TagRef { Text = "shared" }, 0, categories));

        Assert.Contains("Kind, Other", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Ticketing.Models.TicketTag("cat3", "Shared"), InstanceLookup.MatchTag(new TagRef { TagCategoryId = "other", Text = "shared" }, 0, categories));
    }

    [Fact]
    public async Task Update_ticket_status_stops_for_a_ticket_that_doesnt_exist()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.NotFound, """{"error":true,"message":"Not found"}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await Assert.ThrowsAsync<McpException>(() =>
            new TicketTools(client, new FixedActor(Jane), cache, options).UpdateTicketStatus(TicketA, "Doing", cancellationToken: Ct));
        Assert.Single(handler.Requests);
    }
}
