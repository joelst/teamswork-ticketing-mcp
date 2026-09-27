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

/// <summary>Behaviour added in the third review round.</summary>
public sealed class ReviewRound3Tests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string TicketB = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private static readonly TicketUser Actor = new("u1", "Jane Doe", "jane@example.test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Ticket(string id, int number) => $$$"""{"id":"{{{id}}}","ticketNo":{{{number}}}}""";

    // ---- Number lookup: every "absent" rests on one response, and requests are bounded ------------------------------

    [Fact]
    public async Task Short_page_that_isnt_the_end_is_not_proof_of_absence()
    {
        // Numbers 100..45 on a page the API cut short (itemCount says 500 tickets): 42 may be on the next page.
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 100)}}}],"itemCount":500}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 100)}}},{{{Ticket(TicketB, 45)}}}],"itemCount":500}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 45)}}},{{{Ticket(TicketA, 42)}}}],"itemCount":500}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":42}}""");

        await Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct);

        Assert.Equal("1", TestFactory.Query(handler.Requests[3].Uri)["offset"]); // stepped forward, overlapping by a row
    }

    [Fact]
    public async Task End_of_list_is_taken_from_the_total()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 100)}}}],"itemCount":2}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 100)}}},{{{Ticket(TicketB, 60)}}}],"itemCount":2}""");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct));

        Assert.StartsWith("There is no ticket number 42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_page_is_clamped_to_the_reported_total()
    {
        // Numbering that starts high: highest 20000 but only 2000 tickets, so 5 can't be past position 1999.
        var options = TestFactory.Options(o => o.MaxScanTickets = 100);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{{{Ticket(TicketB, 20000)}}}],"itemCount":2000}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[],"itemCount":2000}""");

        await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler, options), options).FindTicketByNumber("5", cancellationToken: Ct));

        Assert.Equal("1951", TestFactory.Query(handler.Requests[2].Uri)["offset"]); // ends at 1999, sized by the 49 left
        Assert.Equal(3, handler.Requests.Count); // the empty page is charged, so the budget is spent
    }

    [Fact]
    public async Task A_matching_number_without_a_usable_id_is_not_reported_missing()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[{"id":null,"ticketNo":42}]}""");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct));

        Assert.Contains("exists, but", ex.Message, StringComparison.Ordinal);
    }

    // ---- Client: outcome and idempotency --------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_dropped_while_the_answer_arrives_is_an_unknown_outcome()
    {
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new DroppingContent() });

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.True(ex.OutcomeUnknown);
    }

    [Theory]
    [InlineData("note", 1)] // a status change with a note isn't repeated
    [InlineData(null, 2)]   // without one, repeating it is harmless
    public async Task Status_change_is_retried_only_without_a_comment(string? comment, int requests)
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.BadGateway, "{}")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","status":"Closed"}}""");

        Task<Ticket> change = TestFactory.Client(handler).UpdateTicketStatusAsync(Guid.Parse(TicketA), "Closed", null, comment, Actor, null, Ct);
        if (requests == 1)
        {
            TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => change);
            Assert.True(ex.OutcomeUnknown); // a note may have been recorded: say so rather than invite a retry
        }
        else
        {
            Assert.Equal("Closed", (await change).Status); // retried, and succeeded
        }

        Assert.Equal(requests, handler.Requests.Count);
    }

    private sealed class DroppingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("connection reset");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Priority_warning_names_the_ticket_by_id_when_it_has_no_number()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}","priority":"Medium"}}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"nope"}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket("Hi", priority: "Low", cancellationToken: Ct));

        Assert.StartsWith($"Ticket {TicketA} was created", doc.RootElement.GetProperty("warning").GetString(), StringComparison.Ordinal);
    }

    // ---- Instance cache: shared reads, and no free flushing -------------------------------------------------------

    [Fact]
    public async Task Callers_that_miss_together_share_one_read()
    {
        var handler = new GatedHandler(gateFirst: true);
        (TicketingClient client, InstanceCache cache) = Gated(handler);

        Task<Instance> first = Task.Run(() => cache.GetInstanceAsync(client, null, refresh: false, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the first read is out
        Task<Instance> second = cache.GetInstanceAsync(client, null, refresh: false, Ct);
        handler.Release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task A_refresh_doesnt_join_a_read_that_started_before_it()
    {
        var handler = new GatedHandler(gateFirst: true);
        (TicketingClient client, InstanceCache cache) = Gated(handler);

        Task<Instance> ordinary = Task.Run(() => cache.GetInstanceAsync(client, -6, refresh: false, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetInstanceAsync(client, -6, refresh: true, Ct); // its own read, not the older one

        Assert.Equal(2, handler.Count);
        handler.Release.SetResult();
        await ordinary;
    }

    [Fact]
    public async Task A_read_older_than_a_refresh_cant_put_its_copy_back()
    {
        var handler = new GatedHandler(gateFirst: true);
        (TicketingClient client, InstanceCache cache) = Gated(handler);

        Task<Instance> older = Task.Run(() => cache.GetInstanceAsync(client, -6, refresh: false, Ct), Ct); // held back
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct); // a refresh finishes first
        handler.Release.SetResult();
        await older; // answers its own caller...

        await cache.GetInstanceAsync(client, -6, refresh: false, Ct); // ...but wasn't cached, so this reads again
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Cycling_offsets_doesnt_multiply_refreshes()
    {
        var handler = new GatedHandler(gateFirst: false);
        (TicketingClient client, InstanceCache cache) = Gated(handler);

        await cache.GetInstanceAsync(client, -6, refresh: true, Ct);  // honoured: a read
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct);  // within 30 s of it: an ordinary read of -5
        await cache.GetInstanceAsync(client, -6, refresh: true, Ct);  // ordinary: -6 is still cached
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct);  // ordinary: -5 is now cached

        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task A_new_caller_doesnt_join_a_read_from_before_a_refresh()
    {
        var handler = new GatedHandler(gateFirst: true);
        (TicketingClient client, InstanceCache cache) = Gated(handler);

        Task<Instance> older = Task.Run(() => cache.GetInstanceAsync(client, -6, refresh: false, Ct), Ct); // held back
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetInstanceAsync(client, -5, refresh: true, Ct); // a refresh finishes
        await cache.GetInstanceAsync(client, -6, refresh: false, Ct); // its own read, not the older one

        Assert.Equal(3, handler.Count);
        handler.Release.SetResult();
        await older;
    }

    private static (TicketingClient Client, InstanceCache Cache) Gated(GatedHandler handler)
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        return (ClientFor(handler), new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time));
    }

    /// <summary>Answers every request with the instance; the first can be held until released, asynchronously.</summary>
    private sealed class GatedHandler(bool gateFirst) : HttpMessageHandler
    {
        private int _count;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1 && gateFirst)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, InstanceJson);
        }
    }

    [Theory]
    [InlineData("654d8ff9-bfdc-448a-ba40-2c15a2b2f6a4", "pat@example.test", true)]
    [InlineData("pat@example.test", "pat@example.test", true)] // the email-to-ticket form
    [InlineData("00000000-0000-0000-0000-000000000000", "pat@example.test", false)]
    [InlineData("not-an-email", "not-an-email", false)] // the email form needs a real email
    [InlineData("654d8ff9-bfdc-448a-ba40-2c15a2b2f6a4", "Pat <pat@example.test>", false)] // a display-name form isn't an address
    public void Service_account_identity_must_be_one_the_help_desk_can_know(string id, string email, bool valid)
    {
        Assert.Equal(valid, new ServiceAccountOptions { Id = id, Name = "Pat", Email = email }.IsValidIdentity);
    }

    [Theory]
    [InlineData("""{"frt":{"urgent":{"enabled":false},"newRule":{"mode":"x"}},"rt":{"low":{"enabled":false}}}""")] // an unknown rule
    [InlineData("""{"frt":{"urgent":{"enabled":false}}}""")] // no resolution rules at all
    [InlineData("""{"frt":{"urgent":{"enabled":"no"}},"rt":{"low":{"enabled":false}}}""")] // not a boolean
    public void Partly_recognised_sla_settings_are_unknown_not_off(string json)
    {
        Assert.Null(WorkloadTools.SlaEnabled(JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public void A_link_loop_under_the_upload_root_stops_startup()
    {
        using var dir = new TempDir();
        string a = Path.Combine(dir.Path, "a");
        string b = Path.Combine(dir.Path, "b");
        try
        {
            Directory.CreateSymbolicLink(a, b);
            Directory.CreateSymbolicLink(b, a);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating a symbolic link needs a privilege this machine doesn't grant.");
        }

        Assert.Throws<StartupConfigurationException>(() => UploadFolder.Canonical(Path.Combine(a, "uploads")));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_cached_copy()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"quota"}""");
        (TicketingClient client, InstanceCache cache, _) = Build(handler, time: time);

        await cache.GetInstanceAsync(client, null, refresh: false, Ct);
        time.Advance(InstanceCache.MinRefreshInterval + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TicketingApiException>(() => cache.GetInstanceAsync(client, null, refresh: true, Ct));

        Instance still = await cache.GetInstanceAsync(client, null, refresh: false, Ct); // no request: the copy survived
        Assert.Equal("Help desk", still.DisplayName);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task One_call_reads_the_instance_once_even_uncached()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler, TestFactory.Options(o => o.InstanceCacheSeconds = 0));

        await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket(
            "Hi",
            assignee: new UserRef(Email: "john@example.test"),
            customFields: new Dictionary<string, JsonElement> { ["Location"] = JsonSerializer.SerializeToElement("HQ") },
            cancellationToken: Ct);

        Assert.Equal(2, handler.Requests.Count); // one instance read for both lookups, then the create
    }

    // ---- People ---------------------------------------------------------------------------------------------------

    private static Instance WithPeople(string peoples) =>
        JsonSerializer.Deserialize<ItemResponse<Instance>>("""{"item":{"id":"i","assignees":{"peoples":[""" + peoples + "]}}}", TicketingClient.JsonOptions)!.Item!;

    private static readonly Instance Listed = WithPeople("""{"id":"u1","name":"Jane Doe","email":"jane@example.test"}""");

    [Fact]
    public void An_empty_assignee_list_lets_no_assignee_through()
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("x", "X", "x@example.test"), "assignee", WithPeople(""), assigneeOnly: true));

        Assert.Contains("assignee list is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outsider_cant_use_a_listed_persons_name()
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("x", "Jane Doe", "jane@evil.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("uses the name of Jane Doe", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pat@contoso.com", true)]
    [InlineData("pat@CONTOSO.com", true)]
    [InlineData("pat@evil.test", false)]
    public void Outsiders_must_be_in_an_allowed_domain_when_one_is_set(string email, bool allowed)
    {
        IReadOnlySet<string> domains = TestFactory.Options(o => o.ExternalEmailDomains = "contoso.com, @contoso.co.uk").ExternalEmailDomainSet();
        UserRef person = new(email, email, email);

        if (allowed)
        {
            string canonical = email[..email.IndexOf('@')] + email[email.IndexOf('@')..].ToLowerInvariant(); // domain in lower case
            Assert.Equal(canonical, InstanceLookup.ResolvePerson(person, "requestor", Listed, assigneeOnly: false, domains).Email);
        }
        else
        {
            Assert.Contains("outside the email domains", Assert.Throws<McpException>(() =>
                InstanceLookup.ResolvePerson(person, "requestor", Listed, assigneeOnly: false, domains)).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_ticket_with_someone_elses_id_and_my_email_isnt_mine()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"items":[
              {"id":"a","assignee":{"id":"someone-else","name":"Spoof","email":"jane@example.test"}},
              {"id":"b","assignee":{"id":"jane@example.test","name":"jane@example.test","email":"jane@example.test"}},
              {"id":"c","assignee":{"id":"u1","name":"Jane","email":"old@example.test"}}],"itemCount":3}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListMyTickets(cancellationToken: Ct));

        Assert.Equal(["b", "c"], doc.RootElement.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
    }

    // ---- Custom fields ------------------------------------------------------------------------------------------

    [Fact]
    public void Copies_that_disagree_on_a_fields_type_are_refused()
    {
        Instance instance = JsonSerializer.Deserialize<ItemResponse<Instance>>("""
            {"item":{"id":"i",
              "customFields":[{"id":"11111111-1111-1111-1111-111111111111","title":"Due","type":{"key":"1_text"}}],
              "customFieldsLeft":[{"id":"11111111-1111-1111-1111-111111111111","title":"Due","type":{"key":"3_date"}}]}}
            """, TicketingClient.JsonOptions)!.Item!;

        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.CheckCustomFields(new Dictionary<string, JsonElement> { ["Due"] = JsonSerializer.SerializeToElement("x") }, instance));

        Assert.Contains("defined differently", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_given_twice_is_refused()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["Location"] = JsonSerializer.SerializeToElement("A"),
            ["11111111-1111-1111-1111-111111111111"] = JsonSerializer.SerializeToElement("B"),
        };

        Assert.Contains("more than once", Assert.Throws<McpException>(() => InstanceLookup.CheckCustomFields(values, ParseInstance())).Message, StringComparison.Ordinal);
    }

    // ---- SLA ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sla_turned_off_is_said_plainly_without_scanning()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK,
            """{"item":{"id":"i","sla":{"frt":{"urgent":{"enabled":false},"escalation":{"enabled":false}},"rt":{"low":{"enabled":false}}}}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListSlaRisk(cancellationToken: Ct));

        Assert.Contains("SLA tracking is turned off", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests); // the instance only; no tickets read
    }

    [Fact]
    public async Task Sla_on_but_no_flags_returned_is_said_plainly()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, SlaOnInstanceJson)
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"a"},{"id":"b"}],"itemCount":2}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListSlaRisk(kind: "escalated", cancellationToken: Ct));

        Assert.Contains("none of the tickets read carried SLA flags", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.False(TestFactory.Query(handler.Requests[1].Uri).ContainsKey("select")); // every mode reads whole tickets
    }

    [Theory]
    [InlineData("""{"frt":{"urgent":{"enabled":true}}}""", true)]
    [InlineData("""{"frt":{"urgent":{"enabled":false}},"rt":{"escalation":{"enabled":false}}}""", false)]
    [InlineData("""{"somethingElse":1}""", null)] // a shape this server doesn't know: scan rather than guess
    public void Sla_state_is_read_from_the_settings(string json, bool? expected)
    {
        Assert.Equal(expected, WorkloadTools.SlaEnabled(JsonSerializer.Deserialize<JsonElement>(json)));
    }

    // ---- Upload folder ------------------------------------------------------------------------------------------

    [Fact]
    public void Upload_folder_may_not_sit_inside_an_application_settings_folder()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(appData), "No application data folder.");
        string root = Path.Combine(appData, "taas-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() =>
                UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = root), null));

            Assert.Contains("application settings folder", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Upload_folder_may_not_sit_inside_a_hidden_home_folder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipWhen(string.IsNullOrEmpty(home), "No home folder.");
        string hidden = Path.Combine(home, ".taas-mcp-tests-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(hidden, "uploads");
        Directory.CreateDirectory(root);
        try
        {
            StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() =>
                UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = root), null));

            Assert.Contains("hidden folder", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(hidden, recursive: true);
        }
    }

    [Fact]
    public void Canonical_doesnt_pick_a_sibling_on_a_case_sensitive_volume()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Inbox"));
        bool caseSensitive = !Directory.Exists(Path.Combine(dir.Path, "INBOX"));
        Assert.SkipUnless(caseSensitive, "This volume ignores case, so a wrong-case name really does name the folder.");
        Directory.CreateDirectory(Path.Combine(dir.Path, "INBOX"));

        string canonical = UploadFolder.Canonical(Path.Combine(dir.Path, "inbox"));

        Assert.EndsWith("inbox", canonical, StringComparison.Ordinal); // kept as written: neither sibling
    }

    [Fact]
    public void Open_file_check_runs_where_the_os_can_answer()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS has no managed way to ask where an open handle points; the path check stands alone.");
        // The real-path check of the open handle runs on Windows and Linux; this upload succeeding shows it accepts a
        // plain file in the folder.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "x");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Single(folder.ReadFiles(["a.txt"]));
    }
}
