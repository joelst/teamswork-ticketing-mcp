using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in response to the PR review and the adversarial reviews.</summary>
public sealed class ReviewFixesTests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string TicketB = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private static readonly TicketUser Actor = new("u1", "Jane Doe", "jane@example.test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Ticket(string id, int number) => $$$"""{"id":"{{{id}}}","ticketNo":{{{number}}}}""";

    // ---- Create outcomes ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_priority_follow_up_returns_the_created_ticket_with_a_warning()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":77,"priority":"Medium"}}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"nope"}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket("Hi", priority: "Low", cancellationToken: Ct));

        Assert.Equal(TicketA, doc.RootElement.GetProperty("item").GetProperty("id").GetString());
        string warning = doc.RootElement.GetProperty("warning").GetString()!;
        Assert.Contains("#77 was created", warning, StringComparison.Ordinal);
        Assert.Contains("Don't create it again", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task A_create_that_may_have_gone_through_says_so(HttpStatusCode status, bool unknown)
    {
        var handler = new FakeHttpHandler().Enqueue(status, """{"message":"x"}""");
        TicketingClient client = TestFactory.Client(handler);

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            client.CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.Equal(unknown, ex.OutcomeUnknown);
        Assert.Equal(unknown, ex.Message.Contains("check before trying again", StringComparison.Ordinal));
        Assert.Single(handler.Requests); // never retried blindly
    }

    [Fact]
    public async Task Create_success_without_a_ticket_is_an_unknown_outcome()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"item":null,"error":false}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.True(ex.OutcomeUnknown);
        Assert.DoesNotContain("Check the inputs and try again", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_update_is_not_marked_unknown()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.InternalServerError, "{}")
            .Enqueue(HttpStatusCode.InternalServerError, "{}")
            .Enqueue(HttpStatusCode.InternalServerError, "{}");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).UpdateTicketAsync(Guid.Parse(TicketA), new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.False(ex.OutcomeUnknown); // an update can safely be repeated
    }

    // ---- Custom field values ----------------------------------------------------------------------------------

    private const string TypedInstanceJson = """
        {"item":{"id":"i",
          "customFields":[
            {"id":"11111111-1111-1111-1111-111111111111","title":"Due","type":{"key":"3_date"}},
            {"id":"22222222-2222-2222-2222-222222222222","title":"Approver","type":{"key":"6_peoplepicker"},"isMultiple":true},
            {"id":"33333333-3333-3333-3333-333333333333","title":"Rating","type":{"key":"9_stars"}},
            {"id":"44444444-4444-4444-4444-444444444444","title":"Site","type":{"key":"1_text"},"status":"visible"},
            {"id":"55555555-5555-5555-5555-555555555555","title":"Site","type":{"key":"1_text"},"status":"hidden"}],
          "customFieldsLeft":[
            {"id":"66666666-6666-6666-6666-666666666666","title":"Asset","type":{"key":"1_text"}}],
          "customFieldsRight":[
            {"id":"66666666-6666-6666-6666-666666666666","title":"Asset","type":{"key":"1_text"},"status":"hidden"}],
          "assignees":{"type":"specific","peoples":[
            {"id":"u1","name":"Jane Doe","email":"jane@example.test"},
            {"id":"u2","name":"John Smith","email":"john@example.test"}]}}}
        """;

    private static Instance Typed() => JsonSerializer.Deserialize<ItemResponse<Instance>>(TypedInstanceJson, TicketingClient.JsonOptions)!.Item!;

    private static JsonElement Check(string json) =>
        InstanceLookup.CheckCustomFields(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, Typed());

    [Theory]
    [InlineData("""{"Due":"2026-13-01"}""")]
    [InlineData("""{"Due":"2026-04-01T09:00:00"}""")]
    [InlineData("""{"Due":"next week"}""")]
    public void Date_fields_take_only_real_dates(string json)
    {
        Assert.Contains("YYYY-MM-DD", Assert.Throws<McpException>(() => Check(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Date_fields_are_normalised()
    {
        Assert.Equal("2026-04-01", Check("""{"Due":" 2026-04-01 "}""").GetProperty("11111111-1111-1111-1111-111111111111").GetString());
    }

    [Fact]
    public void People_picker_entries_are_resolved_like_people()
    {
        JsonElement people = Check("""{"Approver":["john@example.test",{"name":"Jane"}]}""").GetProperty("22222222-2222-2222-2222-222222222222");

        Assert.Equal("u2", people[0].GetProperty("id").GetString());
        Assert.Equal("u1", people[1].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("""{"Approver":[{"id":"u2","name":"John Smith","email":"attacker@evil.test"}]}""", "mixes the ID and email")]
    [InlineData("""{"Approver":[42]}""", "must be an email")]
    [InlineData("""{"Rating":5}""", "can't check")]
    [InlineData("""{"Asset":"x"}""", "is hidden")] // hidden in one of its copies
    public void Unsafe_custom_field_values_are_refused(string json, string message)
    {
        Assert.Contains(message, Assert.Throws<McpException>(() => Check(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_prefers_the_usable_field()
    {
        Assert.Equal("HQ", Check("""{"Site":"HQ"}""").GetProperty("44444444-4444-4444-4444-444444444444").GetString());
    }

    // ---- People -----------------------------------------------------------------------------------------------

    [Fact]
    public void Assignee_must_be_in_the_assignee_list()
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("x", "Outsider", "out@example.test"), "assignee", Typed(), assigneeOnly: true));

        Assert.Contains("can only be assigned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requestor_outside_the_list_is_allowed_as_given()
    {
        TicketUser user = InstanceLookup.ResolvePerson(new UserRef("out@example.test", "out@example.test", "out@example.test"), "requestor", Typed(), assigneeOnly: false);

        Assert.Equal("out@example.test", user.Id);
    }

    [Fact]
    public void A_known_person_given_complete_gets_their_own_details()
    {
        TicketUser user = InstanceLookup.ResolvePerson(new UserRef("john@example.test", "whatever", "JOHN@example.test"), "requestor", Typed(), assigneeOnly: false);

        Assert.Equal(("u2", "John Smith"), (user.Id, user.Name)); // the email-to-ticket form of a known person
    }

    [Fact]
    public void Mixed_identity_is_refused_even_for_a_requestor()
    {
        Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("u1", "Jane Doe", "someone@else.test"), "requestor", Typed(), assigneeOnly: false));
    }

    [Theory]
    [InlineData("oh")] // inside "John", not the start of a word
    [InlineData("ane")]
    public void Partial_names_must_start_a_word(string name)
    {
        Assert.Throws<McpException>(() => InstanceLookup.MatchPerson(new UserRef(Name: name), "assignee", Typed()));
    }

    [Fact]
    public async Task A_name_missing_from_the_cache_is_looked_up_again()
    {
        const string before = """{"item":{"id":"i","assignees":{"peoples":[{"id":"u1","name":"Jane Doe","email":"jane@example.test"}]}}}""";
        var time = new MutableTime(DateTimeOffset.UtcNow);
        const string after = """{"item":{"id":"i","assignees":{"peoples":[{"id":"u1","name":"Jane Doe","email":"jane@example.test"},{"id":"u9","name":"New Hire","email":"new@example.test"}]}}}""";
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, before)
            .Enqueue(HttpStatusCode.OK, after)
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler, time: time);
        await cache.GetInstanceAsync(client, null, refresh: false, Ct); // cached a while ago...
        time.Advance(InstanceCache.MinRefreshInterval + TimeSpan.FromSeconds(1)); // ...long enough for a refresh to be honoured

        await new TicketTools(client, new FixedActor(Jane), cache, options).AssignTicket(TicketA, "new@example.test", cancellationToken: Ct);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("u9", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_description_leaves_the_html_in_charge()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket("Hi", description: "   ", descriptionHtml: "<p>x</p>", cancellationToken: Ct);

        Assert.Contains("\"description_HTML\":\"\\u003Cp\\u003Ex\\u003C/p\\u003E\"", handler.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Cache ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_drops_every_offset()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        (TicketingClient client, InstanceCache cache, _) = Build(handler);

        await cache.GetInstanceAsync(client, -6, refresh: false, Ct);
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct);
        await cache.GetInstanceAsync(client, -6, refresh: false, Ct); // refetched: the refresh dropped it

        Assert.Equal(3, handler.Requests.Count);
    }

    // ---- Scans ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scan_without_a_total_keeps_paging_while_pages_are_full()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"3"}]}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.Equal(3, r.Scanned);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Scan_keeps_paging_after_a_short_page_when_the_total_says_so()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"}],"itemCount":2}""") // asked for 5, got 1
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"2"}],"itemCount":2}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 5, Ct);

        Assert.Equal(2, r.Scanned);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Scan_counts_a_repeated_ticket_once()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":4}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":4}"""); // an API ignoring offset

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.Equal(2, r.Scanned);
        Assert.Equal(2, r.Matches.Count);
        Assert.True(r.Truncated); // the API said 4 and repeated itself, so the rest couldn't be reached
    }

    [Fact]
    public async Task Count_by_assignee_groups_one_person_under_one_key()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"items":[
              {"id":"a","assignee":{"id":"u1","name":"Jane Doe","email":"jane@example.test"}},
              {"id":"b","assignee":{"id":"u1","name":"Jane D.","email":"JANE@example.test"}},
              {"id":"c","assignee":{"id":"u3","name":"No Mail","email":""}},
              {"id":"d"}],"itemCount":4}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).CountTickets("assignee", cancellationToken: Ct));

        JsonElement groups = doc.RootElement.GetProperty("groups");
        Assert.Equal(3, groups.GetArrayLength());
        Assert.Equal("Jane Doe <jane@example.test>", groups[0].GetProperty("key").GetString());
        Assert.Equal(2, groups[0].GetProperty("count").GetInt32());
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("key").GetString() == "No Mail"); // an ID but no email is still a person
    }

    // ---- Ticket number lookup ----------------------------------------------------------------------------------

    [Fact]
    public async Task Number_lookup_steps_back_past_deleted_tickets()
    {
        // Highest 3000; 2000 would be at position 1000 at most, but 1500 tickets above it were deleted, so the first page
        // (positions 1..1000) holds only numbers below it and the lookup steps back.
        var options = TestFactory.Options(o => o.MaxScanTickets = 3000);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 3000)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 1999)}},{{Ticket(TicketB, 1000)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 3000)}},{{Ticket(TicketA, 2000)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":2000}}""");

        await Lookup(TestFactory.Client(handler, options), options).FindTicketByNumber("2000", cancellationToken: Ct);

        Assert.Equal("1", TestFactory.Query(handler.Requests[2].Uri)["offset"]);
        Assert.False(TestFactory.Query(handler.Requests[3].Uri).ContainsKey("offset"));
        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[4].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    // Seen live: ticket 12 of 2034, which search doesn't find. The search uses 50 of the 1000-ticket budget, and the
    // first page must still end at the ticket's likeliest position (2034 - 12) rather than 51 places short of it.
    [Fact]
    public async Task First_number_page_ends_at_the_likeliest_position_within_the_budget()
    {
        string fifty = string.Join(",", Enumerable.Range(0, 50).Select(i => Ticket(TicketB, 1200 + i)));
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{fifty}}}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 2034)}}}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 961)}}},{{{Ticket(TicketA, 12)}}}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":12}}""");

        await Lookup(TestFactory.Client(handler)).FindTicketByNumber("12", cancellationToken: Ct);

        Dictionary<string, string> window = TestFactory.Query(handler.Requests[2].Uri);
        Assert.Equal("900", window["limit"]); // 1000 - 50 for the search - 50 for the newest read
        Assert.Equal("1123", window["offset"]); // ends at position 2022 = 2034 - 12
    }

    [Fact]
    public async Task Number_between_two_seen_numbers_is_certainly_absent()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 100)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 100)}},{{Ticket(TicketB, 43)}},{{Ticket(TicketB, 41)}}]}""");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct));

        Assert.StartsWith("There is no ticket number 42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Number_lookup_that_runs_out_of_budget_says_so()
    {
        var options = TestFactory.Options(o => o.MaxScanTickets = 3);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 9)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 100)}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"items":[{{Ticket(TicketB, 20)}}]}""");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler, options), options).FindTicketByNumber("42", cancellationToken: Ct));

        Assert.Contains("may still exist", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // each request is charged what it asks for: the search took the whole budget of 3
    }

    // ---- Similar tickets and context ------------------------------------------------------------------------------

    [Fact]
    public async Task Similar_tickets_with_a_title_of_short_words_still_compare()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"a","title":"PC won't boot"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");

        using JsonDocument doc = JsonDocument.Parse(await Lookup(TestFactory.Client(handler)).FindSimilarTickets("PC", cancellationToken: Ct));

        Assert.Equal(1.0, doc.RootElement.GetProperty("matches")[0].GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task Context_reports_the_real_failure_and_cancels_the_rest()
    {
        var handler = new DelayedHandler();

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(ClientFor(handler)).GetTicketContext(TicketA, cancellationToken: Ct));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal); // the ticket's failure, not a sibling's cancellation
        Assert.Equal(0, handler.Completed); // the other reads were cancelled (each would otherwise wait 30 seconds)
    }

    /// <summary>404 for the ticket at once; the other two wait until cancelled.</summary>
    private sealed class DelayedHandler : HttpMessageHandler
    {
        private int _completed;

        public int Completed => Volatile.Read(ref _completed);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/activities", StringComparison.Ordinal) && !path.EndsWith("/attachments", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":true,"message":"no such ticket"}""");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            Interlocked.Increment(ref _completed);
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[]}""");
        }
    }

    // ---- Quota and IDs ------------------------------------------------------------------------------------------

    [Fact]
    public void Each_caller_has_their_own_share_of_upstream_requests()
    {
        using var quota = new UpstreamQuota(Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 2)));

        quota.Acquire("tenant/alice")?.Dispose();
        quota.Acquire("tenant/alice")?.Dispose();
        TicketingApiException ex = Assert.Throws<TicketingApiException>(() => quota.Acquire("tenant/alice"));

        Assert.Contains("your share", ex.Message, StringComparison.Ordinal);
        quota.Acquire("tenant/bob")?.Dispose(); // someone else is unaffected
        Assert.Null(quota.Acquire(null)); // single-user modes aren't partitioned
    }

    [Fact]
    public async Task Client_charges_every_upstream_request_to_the_caller()
    {
        var options = TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1);
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(options);
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        using var quota = new UpstreamQuota(opts);
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, time),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, time, quota, new Caller("tenant/alice"));

        await client.ListTicketsAsync(new TicketListQuery(), Ct);
        await Assert.ThrowsAsync<TicketingApiException>(() => client.ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.Single(handler.Requests);
    }

    private sealed record Caller(string? Key) : IUpstreamCaller;

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(".")]
    public void Activity_ids_cannot_navigate_the_api_path(string id)
    {
        Assert.Throws<McpException>(() => ToolValidation.RequirePathId(id, "activityId"));
    }

    [Fact]
    public void Real_activity_ids_are_accepted()
    {
        Assert.Equal("010526072411735976", ToolValidation.RequirePathId("010526072411735976", "activityId"));
    }

    // ---- Upload folder ------------------------------------------------------------------------------------------

    [Fact]
    public void Upload_paths_must_match_the_stored_name_exactly()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Shot.png"), "x");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Single(folder.ReadFiles(["Shot.png"]));
        Assert.Throws<McpException>(() => folder.ReadFiles(["shot.png"])); // another spelling, even where the volume ignores case
        Assert.Throws<McpException>(() => folder.ReadFiles([Path.Combine(dir.Path.ToUpperInvariant(), "Shot.png")]));
        Assert.Throws<McpException>(() => folder.ReadFiles(["Sh*t.png"]));
    }

    [Fact]
    public void Upload_folder_refuses_alternate_streams_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Alternate data streams exist only on Windows.");
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "doc.pdf"), "x");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Throws<McpException>(() => folder.ReadFiles(["doc.pdf:Zone.Identifier"]));
    }

    [Fact]
    public void Upload_folder_refuses_links()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(dir.Path, "linked"), outside.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating a symbolic link needs a privilege this machine doesn't grant.");
        }

        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Contains("link", Assert.Throws<McpException>(() => folder.ReadFiles(["linked/secret.txt"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_folder_may_not_contain_protected_folders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? parent = string.IsNullOrEmpty(home) ? null : Path.GetDirectoryName(home);
        Assert.SkipWhen(parent is null || Path.GetPathRoot(parent) == parent, "The home folder's parent is a drive root (HOME=/root), which is refused for that reason first.");

        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() =>
            UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = Path.GetDirectoryName(home)), null));

        Assert.Contains("home folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_spells_existing_segments_as_stored()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Inbox"));

        // A wrong-case spelling only names the folder where the volume ignores case, which the volume, not the operating
        // system, decides (APFS and Windows folders can be case-sensitive).
        string spelling = Directory.Exists(Path.Combine(dir.Path, "INBOX")) ? "INBOX" : "Inbox";
        string canonical = UploadFolder.Canonical(Path.Combine(dir.Path, spelling, "new"));

        Assert.EndsWith(Path.Combine("Inbox", "new"), canonical, StringComparison.Ordinal);
    }
}
