using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the fourth review round.</summary>
public sealed class ReviewRound4Tests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string JaneId = "654d8ff9-bfdc-448a-ba40-2c15a2b2f6a4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string ListedInstanceJson =
        "{\"item\":{\"id\":\"i\",\"assignees\":{\"peoples\":[{\"id\":\"" + JaneId + "\",\"name\":\"Jane Doe\",\"email\":\"jane@contoso.com\"}]}}}";

    private static readonly Instance Listed = JsonSerializer.Deserialize<ItemResponse<Instance>>(
        ListedInstanceJson,
        TicketingClient.JsonOptions)!.Item!;

    // ---- One address, checked and used ----------------------------------------------------------------------------

    [Theory]
    [InlineData("mallory@evil.test, jane@contoso.com")] // a list: the parser reports the last address
    [InlineData("<mallory@evil.test> jane@contoso.com")]
    [InlineData("Jane Doe <jane@contoso.com>")]           // a display name
    [InlineData(" jane@contoso.com")]                     // trimmed by RequireText, so this one is fine...
    public void Only_one_plain_address_is_an_email(string email)
    {
        if (email.Trim() == "jane@contoso.com")
        {
            Assert.Equal("jane@contoso.com", ToolValidation.RequireEmail(email, "email"));
            return;
        }

        Assert.Contains("one plain email address", Assert.Throws<McpException>(() => ToolValidation.RequireEmail(email, "email")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crafted_address_cant_slip_past_the_domain_allowlist()
    {
        IReadOnlySet<string> domains = TestFactory.Options(o => o.ExternalEmailDomains = "contoso.com").ExternalEmailDomainSet();
        var person = new UserRef("11111111-1111-1111-1111-111111111111", "Mallory", "mallory@evil.test, pat@contoso.com");

        Assert.Throws<McpException>(() => InstanceLookup.ResolvePerson(person, "requestor", Listed, assigneeOnly: false, domains));
    }

    [Fact]
    public void Internationalised_domains_are_compared_in_one_form()
    {
        IReadOnlySet<string> domains = TestFactory.Options(o => o.ExternalEmailDomains = "bücher.example").ExternalEmailDomainSet();
        var person = new UserRef("pat@xn--bcher-kva.example", "pat@xn--bcher-kva.example", "pat@xn--bcher-kva.example");

        Assert.Equal("pat@xn--bcher-kva.example", InstanceLookup.ResolvePerson(person, "requestor", Listed, assigneeOnly: false, domains).Email);
    }

    // ---- IDs and names --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("654d8ff9bfdc448aba402c15a2b2f6a4")]      // N form
    [InlineData("{654d8ff9-bfdc-448a-ba40-2c15a2b2f6a4}")] // braces
    [InlineData("654D8FF9-BFDC-448A-BA40-2C15A2B2F6A4")]   // upper case
    public void A_listed_persons_id_is_recognised_however_its_written(string id)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef(id, "Someone", "someone@evil.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("mixes the ID and email", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jane  Doe")]           // two spaces
    [InlineData("Jane Doe")]       // no-break space
    [InlineData("Jane Doe​")]      // zero-width space
    [InlineData("ＪＡＮＥ ＤＯＥ")]      // full-width letters
    public void A_listed_persons_name_is_recognised_however_its_spelled(string name)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("22222222-2222-2222-2222-222222222222", name, "pat@evil.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("uses the name of Jane Doe", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("33333333333333333333333333333333")] // a GUID, but not in the standard form
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void An_outsiders_id_must_be_a_standard_object_id_or_the_email(string id)
    {
        Assert.Contains("Entra object ID", Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef(id, "Pat", "pat@outside.test"), "requestor", Listed, assigneeOnly: false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_assignee_list_is_read_again_before_refusing()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"item":{"id":"i","assignees":{"peoples":[]}}}""")
            .Enqueue(HttpStatusCode.OK, ListedInstanceJson)
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler, time: time);
        await cache.GetInstanceAsync(client, null, refresh: false, Ct);
        time.Advance(InstanceCache.MinRefreshInterval + TimeSpan.FromSeconds(1));

        await new TicketTools(client, new FixedActor(Jane), cache, options).AssignTicket(TicketA, "jane@contoso.com", cancellationToken: Ct);

        Assert.Equal(3, handler.Requests.Count);
    }

    // ---- My tickets as an email-form service account ----------------------------------------------------------------

    [Fact]
    public async Task An_email_form_caller_matches_tickets_by_email()
    {
        var me = new TeamsWork.Ticketing.Mcp.Auth.ActingUser("jane@contoso.com", "Jane", "jane@contoso.com", TeamsWork.Ticketing.Mcp.Auth.ActingUserSource.ServiceAccount);
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, $$$"""
            {"items":[{"id":"a","assignee":{"id":"{{{JaneId}}}","name":"Jane","email":"jane@contoso.com"}},
                      {"id":"b","assignee":{"id":"{{{TicketA}}}","name":"Other","email":"other@contoso.com"}}],"itemCount":2}
            """);
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(me), cache, options).ListMyTickets(cancellationToken: Ct));

        Assert.Equal(["a"], doc.RootElement.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
    }

    // ---- Custom field copies ----------------------------------------------------------------------------------------

    [Fact]
    public void A_copy_that_leaves_an_attribute_out_doesnt_conflict_and_the_stated_one_is_used()
    {
        Instance instance = JsonSerializer.Deserialize<ItemResponse<Instance>>("""
            {"item":{"id":"i",
              "customFields":[{"id":"11111111-1111-1111-1111-111111111111","title":"Device"}],
              "customFieldsLeft":[{"id":"11111111-1111-1111-1111-111111111111","type":{"key":"list"},"isMultiple":false,
                                   "options":[{"key":"opt1","text":"Laptop"}]}]}}
            """, TicketingClient.JsonOptions)!.Item!;

        JsonElement result = InstanceLookup.CheckCustomFields(new Dictionary<string, JsonElement> { ["Device"] = JsonSerializer.SerializeToElement(new[] { "Laptop" }) }, instance);

        Assert.Equal("opt1", result.GetProperty("11111111-1111-1111-1111-111111111111")[0].GetString());
    }

    // ---- Scans, lookups, SLA ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rows_without_an_id_cant_make_a_repeating_scan_look_complete()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":null},{"id":null}],"itemCount":6}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":null},{"id":null}],"itemCount":6}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 3, Ct);

        Assert.Equal(1, r.Scanned);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task A_row_with_a_usable_id_wins_over_one_without()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, $$$"""{"items":[{"id":null,"ticketNo":42},{"id":"{{{TicketA}}}","ticketNo":42}]}""")
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":42}}""");

        await Lookup(TestFactory.Client(handler)).FindTicketByNumber("42", cancellationToken: Ct);

        Assert.EndsWith($"/tickets/{TicketA}", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sla_scan_goes_ahead_when_the_settings_cant_be_read()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.BadRequest, """{"error":true,"message":"nope"}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"a","isRtBreached":true}],"itemCount":1}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new WorkloadTools(client, new FixedActor(Jane), cache, options).ListSlaRisk(cancellationToken: Ct));

        Assert.Equal(1, doc.RootElement.GetProperty("matched").GetInt32());
    }

    // ---- Cache ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_is_never_weaker_than_an_ordinary_read()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, InstanceJson)
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        (TicketingClient client, InstanceCache cache, _) = Build(handler, TestFactory.Options(o => o.InstanceCacheSeconds = 5), time);

        await cache.GetInstanceAsync(client, null, refresh: false, Ct);
        time.Advance(TimeSpan.FromSeconds(20)); // expired (5 s lifetime), though inside the 30 s refresh interval
        await cache.GetInstanceAsync(client, null, refresh: true, Ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_shared_read_isnt_charged_to_the_caller_who_started_it()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1));
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var quota = new UpstreamQuota(opts);
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, time),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, time, quota, new Caller("tenant/alice"));
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time);

        await client.ListTicketsAsync(new TicketListQuery(), Ct); // alice's one request this minute
        Instance instance = await cache.GetInstanceAsync(client, null, refresh: false, Ct); // still served: a shared read

        Assert.Equal("Help desk", instance.DisplayName);
    }

    private sealed record Caller(string? Key) : IUpstreamCaller;

    [Fact]
    public async Task A_caller_that_has_given_up_starts_no_read()
    {
        var handler = new FakeHttpHandler();
        (TicketingClient client, InstanceCache cache, _) = Build(handler);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetInstanceAsync(client, null, refresh: false, cancelled.Token));

        Assert.Empty(handler.Requests);
    }

    // ---- Upload folder ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_temporary_folder_itself_isnt_an_upload_folder()
    {
        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() =>
            UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = Path.GetTempPath()), null));

        Assert.Contains("temporary folder itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_link_to_a_file_elsewhere_is_refused_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link-count check is Windows only.");
        using var dir = new TempDir();
        using var outside = new TempDir();
        string target = Path.Combine(outside.Path, "secret.txt");
        File.WriteAllText(target, "x");
        Assert.SkipUnless(CreateHardLinkW(Path.Combine(dir.Path, "linked.txt"), target, IntPtr.Zero), "Hard links aren't supported on this volume.");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Contains("hard link", Assert.Throws<McpException>(() => folder.ReadFiles(["linked.txt"])).Message, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);
}
