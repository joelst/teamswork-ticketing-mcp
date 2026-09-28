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

    [Theory]
    [InlineData("jane@\uFF43ontoso.com")] // full-width letters, which IDN mapping would fold into contoso.com
    [InlineData("pat@b\u00FCcher.example")] // an internationalised domain not in its punycode form
    public void An_email_domain_must_be_ascii(string email)
    {
        Assert.Contains("ASCII form", Assert.Throws<McpException>(() => ToolValidation.RequireEmail(email, "email")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_email_is_returned_with_its_domain_in_lower_case()
    {
        Assert.Equal("Pat@contoso.com", ToolValidation.RequireEmail("Pat@CONTOSO.com", "email"));
    }

    [Theory]
    [InlineData("*.contoso.com")]
    [InlineData("b\u00FCcher.example")]
    [InlineData("contoso")]
    public void Domain_allowlist_entries_must_be_plain_ascii_domains(string entry)
    {
        Assert.NotEmpty(TestFactory.Options(o => o.ExternalEmailDomains = "contoso.com, " + entry).InvalidExternalEmailDomains());
    }

    [Fact]
    public void Punycode_domains_are_allowed_in_the_allowlist()
    {
        IReadOnlySet<string> domains = TestFactory.Options(o => o.ExternalEmailDomains = "XN--bcher-kva.example").ExternalEmailDomainSet();
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
    [InlineData("Jane  Doe")]                          // two spaces
    [InlineData("Jane\u00A0Doe")]                     // no-break space
    [InlineData("\uFF2A\uFF41\uFF4E\uFF45 Doe")]   // full-width letters
    [InlineData("JANE DOE")]
    public void A_listed_persons_name_is_recognised_however_its_spelled(string name)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("22222222-2222-2222-2222-222222222222", name, "pat@evil.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("uses the name of Jane Doe", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jane Doe\u200B")]       // zero-width space
    [InlineData("Jane Doe\U000E0041")]   // a tag character, outside the basic plane
    [InlineData("Jane\u034F Doe")]       // combining grapheme joiner
    [InlineData("Jane Doe\u3164")]       // Hangul filler
    [InlineData("Jane Doe\uFE0F")]       // variation selector
    public void An_outsiders_name_cant_hide_invisible_characters(string name)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef("22222222-2222-2222-2222-222222222222", name, "pat@evil.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("invisible characters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outsiders_id_is_sent_in_the_standard_form()
    {
        TicketUser user = InstanceLookup.ResolvePerson(new UserRef("3FA85F64-5717-4562-B3FC-2C963F66AFA6", "Pat", "pat@outside.test"), "requestor", Listed, assigneeOnly: false);

        Assert.Equal("3fa85f64-5717-4562-b3fc-2c963f66afa6", user.Id);
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
    public async Task The_caller_who_starts_a_shared_read_pays_for_it_alone()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxUpstreamRequestsPerCallerPerMinute = 1));
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""")
            .Enqueue(HttpStatusCode.OK, InstanceJson);
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var quota = new UpstreamQuota(opts);
        var caller = new MutableCaller { Key = "tenant/alice" };
        var client = new TicketingClient(new HttpClient(handler), opts, new TicketingRateLimiter(opts), new TimeZoneOffsetResolver(opts, time),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, time, quota, caller);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time, quota, caller);

        await client.ListTicketsAsync(new TicketListQuery(), Ct); // alice's one request this minute
        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => cache.GetInstanceAsync(client, null, refresh: false, Ct));
        Assert.Contains("your share", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // refused before any read went out

        caller.Key = "tenant/bob"; // someone else still can
        Instance instance = await cache.GetInstanceAsync(client, null, refresh: false, Ct);
        Assert.Equal("Help desk", instance.DisplayName);
    }

    private sealed class MutableCaller : IUpstreamCaller
    {
        public string? Key { get; set; }
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

    [Fact]
    public void A_hard_link_to_a_file_elsewhere_is_refused_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This checks the Linux link count (statx).");
        using var dir = new TempDir();
        using var outside = new TempDir();
        string target = Path.Combine(outside.Path, "secret.txt");
        File.WriteAllText(target, "x");
        Assert.SkipUnless(link(Utf8(target), Utf8(Path.Combine(dir.Path, "linked.txt"))) == 0, "Hard links aren't supported here.");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        Assert.Contains("hard link", Assert.Throws<McpException>(() => folder.ReadFiles(["linked.txt"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fifo_is_refused_without_hanging_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs are a Unix thing.");
        using var dir = new TempDir();
        Assert.SkipUnless(mkfifo(Utf8(Path.Combine(dir.Path, "pipe")), 0x1B6) == 0, "Couldn't create a FIFO here.");
        UploadFolder folder = UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = dir.Path), null);

        // Opening a FIFO for reading waits for a writer; the check must refuse it before opening.
        Task<McpException> refused = Task.Run(() => Assert.Throws<McpException>(() => folder.ReadFiles(["pipe"])), Ct);
        McpException ex = await refused.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains("isn't a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploads_are_only_offered_where_the_open_file_can_be_verified()
    {
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), UploadFolder.IsSupported);
    }

    private static byte[] Utf8(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int link(byte[] existing, byte[] newPath);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int mkfifo(byte[] path, uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);
}
