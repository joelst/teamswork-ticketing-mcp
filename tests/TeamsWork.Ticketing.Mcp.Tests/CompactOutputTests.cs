using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>
/// Results shaped for a model that pays for every character: tickets by number, people as strings, nothing internal,
/// long text cut, and short acknowledgments for writes.
/// </summary>
public sealed class CompactOutputTests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A ticket as the API returns it, with the internal fields and duplicate HTML that shouldn't reach the agent.
    private static readonly string FullTicket = $$$"""
        {"item":{"id":"{{{TicketA}}}","ticketNo":2213,"title":"VPN drops","status":"In Progress","priority":"2_Medium",
          "requestor":{"id":"r1","name":"Pat Example","email":"pat@example.test"},
          "assignee":{"id":"u1","name":"Jane Doe","email":"jane@example.test"},
          "createdOn":"2026-11-18T17:30:29.1234567Z","resolution":"7c9e6679-7425-40de-944b-e07fc1f90ae7",
          "description":"{{{new string('x', 1500)}}}","description_HTML":"<p>html</p>",
          "resolvedStatus":["Resolved","Closed"],"isCustomWorkflow":false,"trackingId":"T-1","_ts":1763487029,
          "requestorUnseenEventCnt":7,"assigneeUnseenEventCnt":2,
          "workflow":[{"id":"In Progress","label":"In Progress","nextSteps":[{"targetId":"Resolved","transitionLabel":"Resolve"}]},
                      {"id":"Resolved","label":"Resolved","nextSteps":[]}]}}
        """;

    // ---- Ticket references ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2213")]
    [InlineData("#2213")]
    public async Task Tools_take_the_number_people_quote(string reference)
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":2213}]}""")
            .Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options).GetTicketBrief(reference, activities: 0, cancellationToken: Ct));

        Assert.Equal(2213, doc.RootElement.GetProperty("ticketNo").GetInt32());
        Assert.Equal("2213", TestFactory.Query(handler.Requests[0].Uri)["search"]);
        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ITH-2213")]
    [InlineData("ith-2213")]
    public async Task A_number_with_this_help_desks_prefix_is_taken(string reference)
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","code":"ITH"}}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":2213}]}""")
            .Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options).GetTicketBrief(reference, activities: 0, cancellationToken: Ct));

        Assert.Equal(2213, doc.RootElement.GetProperty("ticketNo").GetInt32());
    }

    [Fact]
    public async Task A_number_with_another_systems_prefix_is_refused()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","code":"ITH"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            new TicketTools(client, new FixedActor(Jane), cache, options).AssignTicket("OPS-2213", "jane@example.test", cancellationToken: Ct));

        Assert.Contains("isn't this help desk's ticket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ITH-2213", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // nothing looked up, nothing changed
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"item":{"id":"i"}}""")] // the live API reports no code
    [InlineData(HttpStatusCode.BadRequest, """{"error":true,"message":"Something odd"}""")]
    public async Task A_prefix_is_refused_when_there_is_no_code_to_check_it_against(HttpStatusCode status, string instance)
    {
        var handler = new FakeHttpHandler().Enqueue(status, instance);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            new LookupTools(client, cache, options).GetTicketBrief("ZZZ-2213", activities: 0, cancellationToken: Ct));

        Assert.Contains("pass the number alone (2213)", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_number_seen_in_an_earlier_response_needs_no_lookup()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":2213,"title":"VPN drops"}]}""")
            .Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        await tools.ListTickets(cancellationToken: Ct);
        await new LookupTools(client, cache, options).GetTicketBrief("#2213", activities: 0, cancellationToken: Ct);

        Assert.Equal(2, handler.Requests.Count); // the list, then the ticket: no search for the number
        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VPN")]
    [InlineData("12-34")]
    [InlineData("#")]
    public void Anything_else_is_refused_with_the_forms_that_work(string reference)
    {
        McpException ex = Assert.Throws<McpException>(() => TicketRefs.ParseNumber(reference));

        Assert.Contains("#2213", ex.Message, StringComparison.Ordinal);
    }

    // ---- One ticket ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_comes_back_compact()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        string json = await new LookupTools(client, cache, options).GetTicketBrief(TicketA, activities: 0, cancellationToken: Ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement t = doc.RootElement;

        Assert.Equal("Pat Example <pat@example.test>", t.GetProperty("requestor").GetString());
        Assert.Equal("Medium", t.GetProperty("priority").GetString());                 // not 2_Medium
        Assert.Equal("2026-11-18T17:30:29Z", t.GetProperty("createdOn").GetString());   // to the second
        Assert.False(t.GetProperty("isResolved").GetBoolean());
        Assert.Contains("[500 more characters]", t.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(7, t.GetProperty("requestorUnseenUpdates").GetInt32());
        Assert.Contains("may not be getting", t.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("Resolved", t.GetProperty("allowedNextStatuses")[0].GetProperty("status").GetString());
        foreach (string hidden in (string[])["workflow", "resolvedStatus", "_ts", "trackingId", "assigneeUnseenEventCnt", "description_HTML", "resolution", "isCustomWorkflow"])
        {
            Assert.False(t.TryGetProperty(hidden, out _), $"{hidden} should be left out");
        }
    }

    [Fact]
    public async Task Html_comes_back_only_when_asked_for()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options).GetTicketBrief(TicketA, activities: 0, includeHtml: true, cancellationToken: Ct));

        Assert.Equal("<p>html</p>", doc.RootElement.GetProperty("description_HTML").GetString());
    }

    // ---- Lists ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_rows_are_summaries_unless_more_is_selected()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":2213,"title":"VPN drops","description":"long","assignee":{"id":"u1","name":"Jane Doe","email":"jane@example.test"}}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketA}}}","ticketId":2213,"title":"VPN drops","description":"long"}]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        using JsonDocument plain = JsonDocument.Parse(await tools.ListTickets(cancellationToken: Ct));
        using JsonDocument selected = JsonDocument.Parse(await tools.ListTickets(select: "description", cancellationToken: Ct));

        JsonElement row = plain.RootElement.GetProperty("items")[0];
        Assert.Equal("Jane Doe <jane@example.test>", row.GetProperty("assignee").GetString());
        Assert.False(row.TryGetProperty("description", out _));
        Assert.False(row.TryGetProperty("id", out _)); // the number is the reference
        Assert.DoesNotContain("description", TestFactory.Query(handler.Requests[0].Uri)["select"], StringComparison.Ordinal);
        Assert.Equal("long", selected.RootElement.GetProperty("items")[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Filtering_by_one_state_names_the_others()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i"}}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options).ListTickets(status: "open", cancellationToken: Ct));

        string hint = doc.RootElement.GetProperty("hint").GetString()!;
        Assert.Contains("Only tickets in 'open'", hint, StringComparison.Ordinal);
        Assert.Contains("In Progress", hint, StringComparison.Ordinal);
        Assert.Contains("isResolved=false", hint, StringComparison.Ordinal);
    }

    // ---- Activities and attachments ------------------------------------------------------------------------------

    [Fact]
    public void An_activity_is_its_new_text_without_the_quoted_email_and_without_html()
    {
        var activity = new Activity
        {
            Id = "a1",
            Comment = "Thanks, that fixed it.\n\nFrom: Jane Doe <jane@example.test>\nSent: Monday, November 18, 2026 10:00 AM\nSubject: Re: help\nOld message",
            CommentHtml = "<p>Thanks</p>",
            CreatedDateTime = "2026-11-18T17:30:29.5Z",
            CreatedBy = new TicketUser("r1", "Pat Example", "pat@example.test"),
            Changes = [new ActivityChange("description", JsonSerializer.SerializeToElement(new string('o', 400)), JsonSerializer.SerializeToElement("new"))],
        };

        JsonElement view = JsonSerializer.SerializeToElement(TicketViews.Activity(activity, includeHtml: false));

        Assert.Equal("Thanks, that fixed it. [quoted reply cut; list_ticket_activities with maxChars=20000 has it]", view.GetProperty("text").GetString());
        Assert.Equal("reply", view.GetProperty("kind").GetString());
        Assert.Equal("Pat Example <pat@example.test>", view.GetProperty("by").GetString());
        Assert.Contains("more characters]", view.GetProperty("changes")[0].GetProperty("from").GetString(), StringComparison.Ordinal);
        Assert.False(view.TryGetProperty("comment_HTML", out _));
    }

    [Fact]
    public void A_file_attachment_has_no_download_url_unless_asked_but_a_link_keeps_its_url()
    {
        var file = new Attachment { Id = "f1", Filename = "log.txt", Src = "https://blob.example/log.txt?sig=secret" };
        var link = new Attachment { Id = "l1", Caption = "Runbook", IsHyperlink = true, Src = "https://docs.example/runbook" };

        Assert.False(JsonSerializer.SerializeToElement(TicketViews.Attachment(file, includeUrl: false)).TryGetProperty("url", out _));
        Assert.Equal(file.Src, JsonSerializer.SerializeToElement(TicketViews.Attachment(file, includeUrl: true)).GetProperty("url").GetString());
        Assert.Equal(link.Src, JsonSerializer.SerializeToElement(TicketViews.Attachment(link, includeUrl: false)).GetProperty("url").GetString());
    }

    // ---- Writes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_answers_with_a_short_acknowledgment()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, FullTicket.Replace(JaneAssigned, JohnAssigned, StringComparison.Ordinal));
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        string json = await new TicketTools(client, new FixedActor(Jane), cache, options)
            .AssignTicket(TicketA, "john@example.test", cancellationToken: Ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2213, doc.RootElement.GetProperty("ticketNo").GetInt32());
        Assert.Equal("John Smith <john@example.test>", doc.RootElement.GetProperty("changed").GetProperty("assignee").GetString());
        Assert.Equal("Jane Doe <jane@example.test>", doc.RootElement.GetProperty("actedAs").GetString());
        Assert.DoesNotContain("xxxx", json, StringComparison.Ordinal); // the description isn't echoed
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 400, $"An acknowledgment should be 400 bytes or less, not {Encoding.UTF8.GetByteCount(json)}.");
        Assert.False(doc.RootElement.TryGetProperty("warning", out _));
    }

    private const string JaneAssigned = "\"assignee\":{\"id\":\"u1\",\"name\":\"Jane Doe\",\"email\":\"jane@example.test\"}";
    private const string JohnAssigned = "\"assignee\":{\"id\":\"u2\",\"name\":\"John Smith\",\"email\":\"john@example.test\"}";

    [Fact]
    public async Task An_acknowledgment_shows_what_the_returned_ticket_has_and_warns_when_it_differs()
    {
        // The API answers with the ticket still assigned to Jane.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, FullTicket);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options)
            .AssignTicket(TicketA, "john@example.test", cancellationToken: Ct));

        Assert.Equal("Jane Doe <jane@example.test>", doc.RootElement.GetProperty("changed").GetProperty("assignee").GetString());
        Assert.Contains("assignee is Jane Doe <jane@example.test>, not John Smith <john@example.test>",
            doc.RootElement.GetProperty("warning").GetString(), StringComparison.Ordinal);
    }

    // ---- Instance settings ---------------------------------------------------------------------------------------

    private const string GraphInstance = """
        {"item":{"id":"i","displayName":"IT","code":"ITH",
          "customFields":[{"id":"11111111-1111-1111-1111-111111111111","title":"Site"}],
          "customFieldsLeft":[{"id":"11111111-1111-1111-1111-111111111111","type":{"key":"list"},"isMultiple":false,"options":[{"key":"o1","text":"HQ"}]}],
          "customFieldsRight":[{"id":"22222222-2222-2222-2222-222222222222","title":"Old","status":"hidden"}],
          "assignees":{"type":"specific","peoples":[{"id":"u1","name":"Jane Doe","email":"jane@example.test"}]},
          "workflows":{"ticket":[{"nodes":[
              {"id":"Ticket Creation","type":"startNode","position":{"x":1,"y":2},"data":{"label":""}},
              {"id":"Open","type":"startNode","position":{"x":3,"y":4},"data":{"label":"Open","color":"#fff"}},
              {"id":"Resolved","type":"textFieldNode","data":{"label":"Resolved"}}],
            "edges":[
              {"id":"e0","label":"Ticket Creation","source":"Ticket Creation","target":"Open",
               "transitionAutomation":{"rules":[{"actions":[{"key":"assignee","value":[{"secondaryText":"someone@example.test"}]}]}]}},
              {"id":"e1","label":"Resolve Ticket","source":"Open","target":"Resolved","data":{"recordComment":true,"authorizedUsers":["owner","assignee"]}}]}]}}}
        """;

    [Fact]
    public async Task Instance_workflows_are_states_and_steps_without_the_editor_graph()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, GraphInstance);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        string json = await new InstanceTools(client, cache).GetInstance(cancellationToken: Ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement workflow = doc.RootElement.GetProperty("workflows")[0];
        Assert.Equal(["Open", "Resolved"], workflow.GetProperty("states").EnumerateArray().Select(s => s.GetString()));
        JsonElement step = Assert.Single(workflow.GetProperty("steps").EnumerateArray());
        Assert.Equal("Resolve Ticket", step.GetProperty("action").GetString());
        Assert.True(step.GetProperty("needsComment").GetBoolean());
        JsonElement[] fields = doc.RootElement.GetProperty("customFields").EnumerateArray().ToArray();
        Assert.Equal(2, fields.Length); // the layout copies are merged into one field each
        Assert.Equal(("Site", "list", "HQ"), (fields[0].GetProperty("title").GetString(), fields[0].GetProperty("type").GetProperty("key").GetString(),
            fields[0].GetProperty("options")[0].GetProperty("text").GetString())); // the type and options only the left copy states
        Assert.Equal("hidden", fields[1].GetProperty("unusable").GetString());
        Assert.Equal("Jane Doe <jane@example.test>", doc.RootElement.GetProperty("assignees").GetProperty("people")[0].GetString());
        foreach (string hidden in (string[])["someone@example.test", "position", "#fff", "customFieldsLeft", "transitionAutomation"])
        {
            Assert.DoesNotContain(hidden, json, StringComparison.Ordinal);
        }
    }

    // The live API returns tickets without their 'workflow' list, so their steps come from the instance's graph.
    private static readonly string TicketWithoutWorkflow = $$$"""
        {"item":{"id":"{{{TicketA}}}","ticketNo":2213,"status":"Open","isCustomWorkflow":false,"resolvedStatus":["Resolved","Closed"]}}
        """;

    [Fact]
    public async Task A_ticket_without_its_workflow_gets_its_steps_from_the_instance()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, TicketWithoutWorkflow)
            .Enqueue(HttpStatusCode.OK, GraphInstance);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new LookupTools(client, cache, options).GetTicketBrief(TicketA, activities: 0, cancellationToken: Ct));

        JsonElement next = Assert.Single(doc.RootElement.GetProperty("allowedNextStatuses").EnumerateArray());
        Assert.Equal("Resolved", next.GetProperty("status").GetString());
        Assert.Equal("Resolve Ticket", next.GetProperty("transition").GetString());
        Assert.True(next.GetProperty("requiresComment").GetBoolean());
        Assert.True(next.GetProperty("resolves").GetBoolean());
        Assert.Equal(["owner", "assignee"], next.GetProperty("allowedRoles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void A_step_the_instance_graph_doesnt_allow_is_refused()
    {
        Ticket ticket = JsonSerializer.Deserialize<ItemResponse<Ticket>>(TicketWithoutWorkflow, TicketingClient.JsonOptions)!.Item!;
        JsonElement? workflows = JsonSerializer.Deserialize<ItemResponse<Instance>>(GraphInstance, TicketingClient.JsonOptions)!.Item!.Workflows;

        Assert.Contains("Allowed: Resolved via 'Resolve Ticket'", Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(ticket, "Closed", null, workflows)).Message, StringComparison.Ordinal);
        Assert.Contains("requires a comment", Assert.Throws<McpException>(() => WorkflowStatus.CheckTarget(ticket, "resolve ticket", null, workflows)).Message, StringComparison.Ordinal);
        Assert.Equal("Resolved", WorkflowStatus.CheckTarget(ticket, "resolve ticket", "Fixed", workflows));
    }

    [Fact]
    public void Two_graphs_with_the_tickets_state_claim_nothing()
    {
        Ticket ticket = JsonSerializer.Deserialize<ItemResponse<Ticket>>(TicketWithoutWorkflow, TicketingClient.JsonOptions)!.Item!;
        JsonElement graph = JsonDocument.Parse("""{"nodes":[{"id":"Open","type":"startNode","data":{"label":"Open"}}],"edges":[]}""").RootElement;
        JsonElement two = JsonSerializer.SerializeToElement(new { ticket = new[] { graph, graph } });

        Assert.Null(WorkflowStatus.AllowedNext(ticket, two));
        Assert.Equal("Anything", WorkflowStatus.CheckTarget(ticket, "Anything", null, two));
    }

    [Fact]
    public void The_status_description_names_the_instances_states()
    {
        Instance instance = JsonSerializer.Deserialize<ItemResponse<Instance>>(GraphInstance, TicketingClient.JsonOptions)!.Item!;

        string text = StatusDescriptions.Describe(instance.Workflows)!;

        Assert.Contains("Open, Resolved", text, StringComparison.Ordinal);
        Assert.Contains("isResolved", text, StringComparison.Ordinal);
        Assert.Null(StatusDescriptions.Describe(null)); // without states, the fixed description stands
    }

    [Fact]
    public void A_tag_that_carries_its_category_keeps_it_so_same_text_tags_can_be_told_apart()
    {
        JsonElement tags = JsonDocument.Parse("""[{"tagCategoryId":"cat1","text":"VPN"},{"tagCategoryId":"cat2","text":"VPN"},"Printer",{"text":"Bare"}]""").RootElement;

        Assert.Equal(
            """[{"text":"VPN","tagCategoryId":"cat1"},{"text":"VPN","tagCategoryId":"cat2"},"Printer","Bare"]""",
            TicketViews.Tags(tags)!.ToJsonString());
    }

    [Fact]
    public async Task A_settings_read_that_outlasts_the_wait_and_then_fails_is_not_started_again_by_each_listing()
    {
        var handler = new HeldInstance(HttpStatusCode.InternalServerError, "{}");
        (TicketingClient client, InstanceCache cache, ServiceProvider services) = DescribeServices(handler);
        using ServiceProvider owned = services;

        Assert.Null(await StatusDescriptions.DescribeAsync(services, Ct, TimeSpan.FromMilliseconds(50)));   // the wait ends first
        handler.Release.SetResult();
        await Assert.ThrowsAsync<TicketingApiException>(() => cache.GetInstanceAsync(client, null, refresh: false, Ct));
        int sent = handler.Requests;

        Assert.Null(await StatusDescriptions.DescribeAsync(services, Ct, TimeSpan.FromMilliseconds(50)));

        Assert.Equal(sent, handler.Requests);
    }

    [Fact]
    public async Task A_settings_read_that_outlasts_the_wait_and_then_succeeds_is_used()
    {
        var handler = new HeldInstance(HttpStatusCode.OK, GraphInstance);
        (TicketingClient client, InstanceCache cache, ServiceProvider services) = DescribeServices(handler);
        using ServiceProvider owned = services;

        Assert.Null(await StatusDescriptions.DescribeAsync(services, Ct, TimeSpan.FromMilliseconds(50)));
        handler.Release.SetResult();
        await cache.GetInstanceAsync(client, null, refresh: false, Ct);

        string? text = await StatusDescriptions.DescribeAsync(services, Ct, TimeSpan.FromMilliseconds(50));

        Assert.Contains("Open, Resolved", text, StringComparison.Ordinal);   // from the cache, though listing backs off
    }

    private static (TicketingClient, InstanceCache, ServiceProvider) DescribeServices(HttpMessageHandler handler)
    {
        IOptions<TicketingOptions> opts = Options.Create(TestFactory.Options());
        var tz = new TimeZoneOffsetResolver(opts, TimeProvider.System);
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), tz,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, TimeProvider.System);
        var cache = new InstanceCache(opts, tz, TimeProvider.System);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton(client)
            .AddSingleton(new StatusDescriptions.BackOff(TimeProvider.System))
            .BuildServiceProvider();
        return (client, cache, services);
    }

    /// <summary>Answers every request once released, so a read can be made to outlast a wait.</summary>
    private sealed class HeldInstance(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _requests;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Tag_categories_are_names_and_usable_tags()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"items":[{"id":"cat1","text":"Area","color":"#123456","tags":[{"text":"Network"},{"text":"Old","deleted":true}]},
                      {"id":"cat2","text":"Gone","deleted":true,"tags":[{"text":"X"}]}]}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        string json = await new InstanceTools(client, cache).ListTagCategories(cancellationToken: Ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement only = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Area", only.GetProperty("name").GetString());
        Assert.Equal(["Network"], only.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.DoesNotContain("#123456", json, StringComparison.Ordinal);
    }

    // ---- Small values --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Pat", "pat@example.test", "Pat <pat@example.test>")]
    [InlineData("pat@example.test", "pat@example.test", "pat@example.test")] // email-to-ticket requestors
    [InlineData(null, "pat@example.test", "pat@example.test")]
    [InlineData("Pat", null, "Pat")]
    [InlineData(" ", "", null)]
    public void People_are_one_string(string? name, string? email, string? expected)
    {
        Assert.Equal(expected, TicketViews.Person(name, email));
    }

    [Theory]
    [InlineData("2_Medium", "Medium")]
    [InlineData("Urgent", "Urgent")]
    public void Priorities_use_the_words_tools_take(string given, string expected)
    {
        Assert.Equal(expected, TicketViews.Priority(given));
    }

    [Fact]
    public void A_resolution_id_is_left_out_and_a_word_kept()
    {
        Assert.Null(TicketViews.Resolution("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
        Assert.Equal("fixed", TicketViews.Resolution("fixed"));
    }
}
