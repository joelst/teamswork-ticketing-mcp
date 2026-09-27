using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class NewToolsTests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string TicketB = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    internal const string InstanceJson = """
        {"item":{"id":"i","displayName":"Help desk",
          "customFields":[
            {"id":"11111111-1111-1111-1111-111111111111","title":"Location","type":{"key":"1_text"}},
            {"id":"22222222-2222-2222-2222-222222222222","title":"Remote","type":{"key":"toggle"}},
            {"id":"33333333-3333-3333-3333-333333333333","title":"Device","type":{"key":"list"},"isMultiple":false,
             "options":[{"key":"opt1","text":"Laptop"},{"key":"opt2","text":"Phone"}]}],
          "workflows":[{"id":"w1"}],
          "assignees":{"type":"specific","peoples":[
            {"id":"u1","name":"Jane Doe","email":"jane@example.test"},
            {"id":"u2","name":"John Smith","email":"john@example.test"},
            {"id":"u3","name":"Johnny Appleseed","email":"johnny@example.test"}]}}}
        """;

    private const string TagsJson = """{"items":[{"id":"cat1","text":"Area","tags":[{"text":"Network"},{"text":"Old","deleted":true}]}]}""";

    private static readonly FixedTimeProvider Time = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    internal static readonly ActingUser Jane = new("u1", "Jane Doe", "jane@example.test", ActingUserSource.DelegatedToken);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Name lookups -----------------------------------------------------------------------------------------

    internal static Instance ParseInstance() =>
        JsonSerializer.Deserialize<ItemResponse<Instance>>(InstanceJson, TicketingClient.JsonOptions)!.Item!;

    private static IReadOnlyList<TagCategory> ParseTags() =>
        JsonSerializer.Deserialize<ListResponse<TagCategory>>(TagsJson, TicketingClient.JsonOptions)!.Items!;

    [Theory]
    [InlineData(null, "jane@EXAMPLE.test", "u1")]
    [InlineData("jane", null, "u1")]
    [InlineData("John Smith", null, "u2")]
    [InlineData("appleseed", null, "u3")]
    public void Person_is_found_by_email_or_name(string? name, string? email, string expectedId)
    {
        TicketUser user = InstanceLookup.MatchPerson(new UserRef(Name: name, Email: email), "assignee", ParseInstance());

        Assert.Equal(expectedId, user.Id);
    }

    [Fact]
    public void Ambiguous_name_lists_the_candidates()
    {
        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.MatchPerson(new UserRef(Name: "John"), "assignee", ParseInstance()));

        Assert.Contains("John Smith <john@example.test>", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Johnny Appleseed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_person_points_at_get_instance()
    {
        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.MatchPerson(new UserRef(Email: "nobody@example.test"), "assignee", ParseInstance()));

        Assert.Contains("get_instance", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Area", "network")]
    [InlineData("cat1", "Network")]
    [InlineData("area", "NETWORK")]
    public void Tag_category_is_found_by_id_or_name_and_text_is_canonicalised(string category, string text)
    {
        TicketTag tag = InstanceLookup.MatchTag(new TagRef(category, text), 0, ParseTags());

        Assert.Equal("cat1", tag.TagCategoryId);
        Assert.Equal("Network", tag.Text);
    }

    [Theory]
    [InlineData("Nope", "Network", "Area")]
    [InlineData("Area", "Old", "Network")] // deleted tags aren't offered
    public void Unknown_tag_or_category_lists_what_exists(string category, string text, string listed)
    {
        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.MatchTag(new TagRef(category, text), 0, ParseTags()));

        Assert.Contains(listed, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_fields_accept_titles_and_option_texts()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["Location"] = JsonSerializer.SerializeToElement("HQ"),
            ["remote"] = JsonSerializer.SerializeToElement(true),
            ["Device"] = JsonSerializer.SerializeToElement(new[] { "laptop" }),
        };

        JsonElement result = InstanceLookup.CheckCustomFields(values, ParseInstance());

        Assert.Equal("HQ", result.GetProperty("11111111-1111-1111-1111-111111111111").GetString());
        Assert.True(result.GetProperty("22222222-2222-2222-2222-222222222222").GetBoolean());
        Assert.Equal("opt1", result.GetProperty("33333333-3333-3333-3333-333333333333")[0].GetString());
    }

    [Theory]
    [InlineData("""{"Remote":"yes"}""", "true or false")]
    [InlineData("""{"Device":["opt1","opt2"]}""", "single value")]
    [InlineData("""{"Device":["Tablet"]}""", "opt1 (Laptop)")]
    [InlineData("""{"Location":5}""", "takes a string")]
    [InlineData("""{"Shoe size":"9"}""", "Location (11111111")]
    public void Custom_field_values_are_checked_against_definitions(string json, string message)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.CheckCustomFields(values, ParseInstance()));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    // Shapes observed from the live API: custom fields also arrive in customFieldsLeft/Right, a hidden field is
    // refused by the API ("not found or invalid definition"), and optional fields are the form's built-in ones.
    private const string LiveShapedInstanceJson = """
        {"item":{"id":"i",
          "customFields":[{"id":"5c505061-5118-4b59-83dd-a1c02e5ea8a3","title":"Device Manufacturer Model","type":{"key":"1_text"},"status":"hidden"}],
          "customFieldsRight":[{"id":"34936031-4f3d-429b-9a2f-551fe87412b9","title":"Time Spent","type":{"key":"1_text"},"status":"visible"}],
          "optionalFieldsLeft":[{"id":"priority","title":"Priority","type":"dropdown"}]}}
        """;

    [Fact]
    public void Custom_fields_in_the_column_lists_are_found()
    {
        Instance instance = JsonSerializer.Deserialize<ItemResponse<Instance>>(LiveShapedInstanceJson, TicketingClient.JsonOptions)!.Item!;
        var values = new Dictionary<string, JsonElement> { ["time spent"] = JsonSerializer.SerializeToElement("1h") };

        JsonElement result = InstanceLookup.CheckCustomFields(values, instance);

        Assert.Equal("1h", result.GetProperty("34936031-4f3d-429b-9a2f-551fe87412b9").GetString());
    }

    [Theory]
    [InlineData("Device Manufacturer Model", "is hidden")]
    [InlineData("Priority", "not a custom field")] // a built-in field, not a custom one
    public void Hidden_and_built_in_fields_are_refused(string key, string message)
    {
        Instance instance = JsonSerializer.Deserialize<ItemResponse<Instance>>(LiveShapedInstanceJson, TicketingClient.JsonOptions)!.Item!;
        var values = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement("x") };

        McpException ex = Assert.Throws<McpException>(() => InstanceLookup.CheckCustomFields(values, instance));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_field_can_be_cleared_with_null()
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Remote":null}""")!;

        JsonElement result = InstanceLookup.CheckCustomFields(values, ParseInstance());

        Assert.Equal(JsonValueKind.Null, result.GetProperty("22222222-2222-2222-2222-222222222222").ValueKind);
    }

    // ---- Instance cache and get_instance -----------------------------------------------------------------------

    [Fact]
    public async Task Instance_is_cached_until_refresh()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        var time = new MutableTime(Time.GetUtcNow());
        (TicketingClient client, InstanceCache cache, _) = Build(handler, time: time);
        var tools = new InstanceTools(client, cache);

        await tools.GetInstance(cancellationToken: Ct);
        await tools.GetInstance(section: "assignees", cancellationToken: Ct);
        Assert.Single(handler.Requests);

        await tools.GetInstance(refresh: true, cancellationToken: Ct);
        Assert.Single(handler.Requests); // read moments ago: a refresh is answered from the copy

        time.Advance(InstanceCache.MinRefreshInterval + TimeSpan.FromSeconds(1));
        await tools.GetInstance(refresh: true, cancellationToken: Ct);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Instance_section_returns_only_that_part()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, InstanceJson);
        (TicketingClient client, InstanceCache cache, _) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new InstanceTools(client, cache).GetInstance(section: "Assignees", cancellationToken: Ct));

        Assert.Equal("Help desk", doc.RootElement.GetProperty("displayName").GetString());
        Assert.True(doc.RootElement.TryGetProperty("assignees", out _));
        Assert.False(doc.RootElement.TryGetProperty("customFields", out _));
        Assert.False(doc.RootElement.TryGetProperty("workflows", out _));
    }

    [Fact]
    public async Task Disabled_cache_reads_every_time()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        (TicketingClient client, InstanceCache cache, _) = Build(handler, TestFactory.Options(o => o.InstanceCacheSeconds = 0));

        await cache.GetInstanceAsync(client, null, refresh: false, Ct);
        await cache.GetInstanceAsync(client, null, refresh: false, Ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- Writes that resolve names ------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ticket_resolves_assignee_tags_and_custom_fields()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, TagsJson)
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":7}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        await tools.CreateTicket(
            "Printer down",
            assignee: new UserRef(Email: "john@example.test"),
            tags: [new TagRef("Area", "network")],
            customFields: new Dictionary<string, JsonElement> { ["Device"] = JsonSerializer.SerializeToElement(new[] { "Phone" }) },
            cancellationToken: Ct);

        Assert.Equal(3, handler.Requests.Count); // instance once (cached for the custom fields), tags, create
        using JsonDocument body = JsonDocument.Parse(handler.Requests[2].Body!);
        JsonElement ticket = body.RootElement.GetProperty("ticket");
        Assert.Equal("u2", ticket.GetProperty("assignee").GetProperty("id").GetString());
        Assert.Equal("John Smith", ticket.GetProperty("assignee").GetProperty("name").GetString());
        Assert.Equal("cat1", ticket.GetProperty("tags")[0].GetProperty("tagCategoryId").GetString());
        Assert.Equal("opt2", ticket.GetProperty("customFields").GetProperty("33333333-3333-3333-3333-333333333333")[0].GetString());
        Assert.Equal("u1", ticket.GetProperty("requestor").GetProperty("id").GetString());
    }

    // Observed live: create ignores priority, so the tool sets it with an update when it didn't stick.
    [Theory]
    [InlineData("Medium", 2)]
    [InlineData("Low", 1)]
    public async Task Create_ticket_reapplies_a_priority_the_api_ignored(string returnedPriority, int expectedRequests)
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}","priority":"{{{returnedPriority}}}"}}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","priority":"Low"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket("Hi", priority: "low", cancellationToken: Ct));

        Assert.Equal("Low", doc.RootElement.GetProperty("item").GetProperty("priority").GetString());
        Assert.Equal(expectedRequests, handler.Requests.Count);
        if (expectedRequests == 2)
        {
            Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
            Assert.Contains("\"priority\":\"Low\"", handler.Requests[1].Body, StringComparison.Ordinal);
        }
    }

    // A complete reference is still checked against the assignee list, so it can't mix two people's details; someone
    // outside the list (an email-to-ticket requestor) is then used as given.
    [Fact]
    public async Task Complete_person_reference_outside_the_list_is_used_as_given()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);
        var tools = new TicketTools(client, new FixedActor(Jane), cache, options);

        await tools.CreateTicket("Hi", requestor: new UserRef("x@example.test", "x@example.test", "x@example.test"), cancellationToken: Ct);

        using JsonDocument body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("x@example.test", body.RootElement.GetProperty("ticket").GetProperty("requestor").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Assign_ticket_resolves_a_name()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        string json = await new TicketTools(client, new FixedActor(Jane), cache, options).AssignTicket(TicketA, "Jane Doe", cancellationToken: Ct);

        CapturedRequest put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        using JsonDocument body = JsonDocument.Parse(put.Body!);
        JsonElement ticket = body.RootElement.GetProperty("ticket");
        Assert.Equal("jane@example.test", ticket.GetProperty("assignee").GetProperty("email").GetString());
        Assert.False(ticket.TryGetProperty("title", out _));
        Assert.Contains("actedAs", json, StringComparison.Ordinal);
    }

    // ---- Scans ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scan_follows_continuation_tokens_and_stops_at_the_cap()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":5,"continuationToken":"t2"}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"3"},{"id":"4"}],"itemCount":5,"continuationToken":"t3"}""");
        TicketingClient client = TestFactory.Client(handler);

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(client, new TicketListQuery(), t => t, maxTickets: 4, pageSize: 2, Ct);

        Assert.Equal(4, r.Scanned);
        Assert.True(r.Truncated);
        Assert.Equal(5, r.Total);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("t2", Assert.Single(handler.Requests[1].Headers.GetValues("continuationToken")));
    }

    [Fact]
    public async Task Scan_falls_back_to_offset_paging_without_a_token()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":3}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"3"}],"itemCount":3}""");
        TicketingClient client = TestFactory.Client(handler);

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(client, new TicketListQuery(), t => t.Id != "2" ? t : null, maxTickets: 100, pageSize: 2, Ct);

        Assert.Equal(3, r.Scanned);
        Assert.False(r.Truncated);
        Assert.Equal(["1", "3"], r.Matches.Select(t => t.Id));
        Assert.Equal("2", TestFactory.Query(handler.Requests[1].Uri)["offset"]);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("requestor", 1)]
    [InlineData("either", 2)]
    public async Task My_tickets_match_the_caller(string? role, int expected)
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"items":[
              {"id":"a","title":"Mine","assignee":{"id":"U1","name":"Jane","email":"JANE@example.test"}},
              {"id":"b","title":"Johns","assignee":{"id":"u2","name":"John","email":"john@example.test"}},
              {"id":"c","title":"Raised","requestor":{"id":"u1","name":"Jane","email":"other@example.test"}}],
             "itemCount":3}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListMyTickets(role, cancellationToken: Ct));

        Assert.Equal(expected, doc.RootElement.GetProperty("matched").GetInt32());
        Dictionary<string, string> q = TestFactory.Query(handler.Requests[0].Uri);
        Assert.Equal("false", q["isResolved"]);
        Assert.Equal("1000", q["limit"]);
        Assert.Contains("assignee", q["select"], StringComparison.Ordinal);
    }

    internal const string SlaOnInstanceJson = """{"item":{"id":"i","sla":{"frt":{"urgent":{"enabled":true}},"rt":{"urgent":{"enabled":false}}}}}""";

    [Fact]
    public async Task Sla_risk_reads_whole_tickets_for_breach_flags()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, SlaOnInstanceJson)
            .Enqueue(HttpStatusCode.OK, """
            {"items":[{"id":"a","isRtBreached":true},{"id":"b","isFrtEscalated":true},{"id":"c"}],"itemCount":3}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListSlaRisk(cancellationToken: Ct));

        Assert.Equal(2, doc.RootElement.GetProperty("matched").GetInt32());
        Dictionary<string, string> q = TestFactory.Query(handler.Requests[1].Uri);
        Assert.False(q.ContainsKey("select"));
        Assert.Equal("200", q["limit"]);
    }

    [Fact]
    public async Task Count_groups_and_sorts()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"items":[{"id":"a","priority":"Urgent"},{"id":"b","priority":"Low"},{"id":"c","priority":"Urgent"}],"itemCount":3}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).CountTickets("priority", cancellationToken: Ct));

        JsonElement groups = doc.RootElement.GetProperty("groups");
        Assert.Equal("Urgent", groups[0].GetProperty("key").GetString());
        Assert.Equal(2, groups[0].GetProperty("count").GetInt32());
        Assert.Equal("Low", groups[1].GetProperty("key").GetString());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Whoami_reports_the_acting_user()
    {
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(new FakeHttpHandler());

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).WhoAmI(Ct));

        Assert.Equal("jane@example.test", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("DelegatedToken", doc.RootElement.GetProperty("source").GetString());
    }

    // ---- Lookups ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ticket_number_found_by_search()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketB}}}","ticketNo":570},{"id":"{{{TicketA}}}","ticketNo":57}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":57,"title":"Found"}}""");

        string json = await Lookup(TestFactory.Client(handler)).FindTicketByNumber("#57", cancellationToken: Ct);

        Assert.Contains("Found", json, StringComparison.Ordinal);
        Assert.Equal("57", TestFactory.Query(handler.Requests[0].Uri)["search"]);
        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticket_number_found_in_the_sorted_window_when_search_misses()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            // A selected response may name the number ticketId.
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketB}}}","ticketId":100}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketB}}}","ticketId":100},{"id":"{{{TicketA}}}","ticketId":42}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":42}}""");

        await Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct);

        Dictionary<string, string> newest = TestFactory.Query(handler.Requests[1].Uri);
        Assert.Equal("ticketId", newest["orderBy"]);
        Assert.Equal("DESC", newest["order"]);
        Dictionary<string, string> window = TestFactory.Query(handler.Requests[2].Uri);
        // Number 42 sits at position 58 at most (100 - 42); the page ending there starts at 0, and it takes what is left
        // of the MaxScanTickets budget (1000, less 50 for the search and 1 for the newest-ticket read).
        Assert.Equal("949", window["limit"]);
        Assert.False(window.ContainsKey("offset")); // offset 0 isn't sent
        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[3].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticket_number_above_the_highest_is_not_found()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":"{{{TicketB}}}","ticketNo":100}]}""");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler)).FindTicketByNumber("500", cancellationToken: Ct));

        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData(TicketA)]
    public void Ticket_number_must_be_a_positive_number(string value)
    {
        Assert.Throws<McpException>(() => LookupTools.ParseTicketNumber(value));
    }

    [Fact]
    public async Task Ticket_context_combines_ticket_activities_and_attachments()
    {
        var handler = new RoutingHandler(path => path switch
        {
            _ when path.EndsWith("/activities", StringComparison.Ordinal) => """{"items":[{"id":"act1","comment":"hello"}],"continuationToken":"older"}""",
            _ when path.EndsWith("/attachments", StringComparison.Ordinal) => """{"items":[{"id":"att1","caption":"log"}]}""",
            _ => $$$"""{"item":{"id":"{{{TicketA}}}","title":"Ctx"}}""",
        });
        TicketingClient client = ClientFor(handler);

        using JsonDocument doc = JsonDocument.Parse(await Lookup(client).GetTicketContext(TicketA, activityLimit: 5, cancellationToken: Ct));

        Assert.Equal("Ctx", doc.RootElement.GetProperty("ticket").GetProperty("title").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("activities")[0].GetProperty("comment").GetString());
        Assert.Equal("older", doc.RootElement.GetProperty("activitiesContinuationToken").GetString());
        Assert.Equal("log", doc.RootElement.GetProperty("attachments")[0].GetProperty("caption").GetString());
        Assert.Equal(3, handler.Paths.Count);
        Assert.Contains(handler.Queries, q => q.Contains("limit=5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Similar_tickets_are_ranked_by_shared_keywords()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"a","title":"VPN disconnects hourly"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"a","title":"VPN disconnects hourly"},{"id":"b","title":"Printer jammed"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");

        using JsonDocument doc = JsonDocument.Parse(await Lookup(TestFactory.Client(handler)).FindSimilarTickets("VPN disconnects every hour", cancellationToken: Ct));

        JsonElement matches = doc.RootElement.GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength()); // the printer ticket shares no keyword
        Assert.Equal(0.5, matches[0].GetProperty("score").GetDouble());
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("false", TestFactory.Query(r.Uri)["isResolved"]));
    }

    [Fact]
    public void Keywords_drop_filler_and_short_words()
    {
        Assert.Equal(["outlook", "crashes", "startup"], LookupTools.Keywords("Outlook crashes on startup - please help!"));
    }

    // ---- Uploads ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_sends_multipart_and_rebuilds_it_for_a_retry()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")
            .Enqueue(HttpStatusCode.Created, """{"item":{"activityId":"act9"}}""");
        TicketingClient client = TestFactory.Client(handler);
        var actor = new TicketUser("u1", "Jane Doe", "jane@example.test");

        CommentActivity result = await client.UploadFilesAsync(
            Guid.Parse(TicketA), [new UploadFile("shot.png", "image/png", "PNGDATA"u8.ToArray())], "See attached", null, isPrivate: true, actor, false, null, Ct);

        Assert.Equal("act9", result.ActivityId);
        Assert.Equal(2, handler.Requests.Count);
        string body = handler.Requests[1].Body!;
        // Quoted, as the live API requires (unquoted names get HTTP 500), and no filename* parameter.
        Assert.Contains("name=\"files\"; filename=\"shot.png\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("filename*", body, StringComparison.Ordinal);
        Assert.Contains("name=\"user\"", body, StringComparison.Ordinal);
        Assert.Contains("PNGDATA", body, StringComparison.Ordinal);
        Assert.Contains("name=\"isPrivate\"", body, StringComparison.Ordinal);
        Assert.Contains("jane@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain(TestFactory.ApiKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_folder_reads_only_plain_files_inside_it()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "ok.txt"), "fine");
        Directory.CreateDirectory(Path.Combine(dir.Path, ".hidden"));
        File.WriteAllText(Path.Combine(dir.Path, ".hidden", "secret.txt"), "no");
        File.WriteAllText(Path.Combine(dir.Path, ".env"), "no");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), userSecretsPath: null);

        UploadFile file = Assert.Single(folder.ReadFiles(["ok.txt"]));
        Assert.Equal("ok.txt", file.FileName);
        Assert.Equal("text/plain", file.ContentType);

        Assert.Contains("outside", Assert.Throws<McpException>(() => folder.ReadFiles(["../outside.txt"])).Message, StringComparison.Ordinal);
        Assert.Contains("outside", Assert.Throws<McpException>(() => folder.ReadFiles([Path.GetTempPath()])).Message, StringComparison.Ordinal);
        Assert.Contains("hidden", Assert.Throws<McpException>(() => folder.ReadFiles([".hidden/secret.txt"])).Message, StringComparison.Ordinal);
        Assert.Contains("hidden", Assert.Throws<McpException>(() => folder.ReadFiles([".env"])).Message, StringComparison.Ordinal);
        Assert.Contains("doesn't exist", Assert.Throws<McpException>(() => folder.ReadFiles(["missing.txt"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_folder_enforces_the_size_limit()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "a.bin"), new byte[600]);
        File.WriteAllBytes(Path.Combine(dir.Path, "b.bin"), new byte[600]);
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => { o.UploadRoot = dir.Path; o.MaxUploadBytes = 1000; }), null);

        Assert.Single(folder.ReadFiles(["a.bin"]));
        Assert.Contains("upload limit", Assert.Throws<McpException>(() => folder.ReadFiles(["a.bin", "b.bin"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_folder_must_exist_and_not_hold_the_secrets_file()
    {
        using var dir = new TempDir();
        string secrets = Path.Combine(dir.Path, "UserSecrets", "secrets.json");

        Assert.Throws<StartupConfigurationException>(() => UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = Path.Combine(dir.Path, "nope")), null));
        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() => UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), secrets));
        Assert.Contains("user-secrets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_file_names_lose_quotes_and_control_characters()
    {
        Assert.Equal("ab.txt", UploadFolder.SafeFileName("a\"\r\nb.txt"));
        Assert.Equal("attachment", UploadFolder.SafeFileName("\"\""));
        Assert.Equal("r_sum_.pdf", UploadFolder.SafeFileName("résumé.pdf"));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    internal static (TicketingClient Client, InstanceCache Cache, IOptions<TicketingOptions> Options) Build(FakeHttpHandler handler, TicketingOptions? options = null, TimeProvider? time = null)
    {
        options ??= TestFactory.Options();
        time ??= Time;
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(options);
        TicketingClient client = TestFactory.Client(handler, options, time);
        return (client, new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time), opts);
    }

    /// <summary>A clock a test can move forward.</summary>
    internal sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    internal static LookupTools Lookup(TicketingClient client, TicketingOptions? options = null) =>
        new(client, Microsoft.Extensions.Options.Options.Create(options ?? TestFactory.Options()));

    internal static TicketingClient ClientFor(HttpMessageHandler handler)
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        return new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, Time),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, Time);
    }

    internal sealed class FixedActor(ActingUser user) : IActingUserProvider
    {
        public ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken) => ValueTask.FromResult(user);
    }

    /// <summary>Answers by path, for tools that make requests in parallel.</summary>
    internal sealed class RoutingHandler(Func<string, string> respond) : HttpMessageHandler
    {
        public ConcurrentBag<string> Paths { get; } = [];

        public ConcurrentBag<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Queries.Add(request.RequestUri.Query);
            return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, respond(request.RequestUri.AbsolutePath)));
        }
    }

    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taas-mcp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
