using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the seventh review round.</summary>
public sealed class ReviewRound7Tests
{
    private const string Outsider = "22222222-2222-2222-2222-222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Instance ListedAs(string name) => JsonSerializer.Deserialize<ItemResponse<Instance>>(
        "{\"item\":{\"id\":\"i\",\"assignees\":{\"peoples\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"" + name +
        "\",\"email\":\"listed@contoso.com\"}]}}}",
        TicketingClient.JsonOptions)!.Item!;

    private static McpException Refused(string listed, string given) => Assert.Throws<McpException>(() =>
        InstanceLookup.ResolvePerson(new UserRef(Outsider, given, "pat@outside.test"), "requestor", ListedAs(listed), assigneeOnly: false));

    // ---- Accents, however they are written -----------------------------------------------------------------------------

    [Theory]
    [InlineData("Jos\u00E9 P\u00E9rez", "Jose\u0301 Pe\u0301rez")] // listed precomposed, given with combining accents
    [InlineData("Jose\u0301 Pe\u0301rez", "Jos\u00E9 P\u00E9rez")] // and the reverse
    [InlineData("Jos\u00E9 P\u00E9rez", "Jose Perez")]             // no accents at all
    [InlineData("\u0141ukasz W\u00F3jcik", "Lukasz Wojcik")]       // a stroke letter, which has no decomposition
    [InlineData("\u039D\u03AF\u03BA\u03BF\u03C2", "\u039D\u03B9\u0301\u03BA\u03BF\u03C2")] // Greek with tonos, both ways of writing it
    [InlineData("Jane Doe", "\u0301Jane Doe")]                     // a mark on nothing, at the start
    public void Accented_spellings_of_a_listed_name_are_the_same_name(string listed, string given)
    {
        Assert.Contains("uses the name of", Refused(listed, given).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vowel_signs_in_other_scripts_are_part_of_the_name()
    {
        // सुमित (Sumit) and समित (Samit) differ only by a vowel sign, a combining mark that isn't an accent.
        TicketUser user = InstanceLookup.ResolvePerson(
            new UserRef(Outsider, "\u0938\u092E\u093F\u0924", "samit@outside.test"), "requestor", ListedAs("\u0938\u0941\u092E\u093F\u0924"), assigneeOnly: false);

        Assert.Equal("\u0938\u092E\u093F\u0924", user.Name);
    }

    [Fact]
    public void The_base_letter_table_only_maps_letters_to_letters()
    {
        foreach (int c in Enumerable.Range(0x00C0, 0x1FFF - 0x00C0))
        {
            int b = BaseLetters.Of(c);
            if (b != c)
            {
                Assert.True(char.IsLetter((char)b), $"U+{c:X4} maps to U+{b:X4}, which isn't a letter.");
            }
        }

        Assert.Equal('e', BaseLetters.Of('\u00E9'));
        Assert.Equal('A', BaseLetters.Of('\u01FA')); // Ǻ: two accents
        Assert.Equal('\u0438', BaseLetters.Of('\u0439')); // й to и
    }

    [Theory]
    [InlineData("David Smith", "\u216Eavid Smith")] // a Roman numeral D
    [InlineData("Alice Brown", "\u03B1lice Brown")] // a Greek alpha
    [InlineData("Sofia Lopez", "So\uFB01a Lopez")]  // the fi ligature
    [InlineData("Lin Wei", "\u04CFin Wei")]         // a Cyrillic palochka
    [InlineData("Zo\u00EB Adams", "Zoe\u0308 Adams")] // a precomposed diaeresis against a combining one
    public void Confusable_letters_from_the_unicode_data_are_caught(string listed, string given)
    {
        Assert.Contains("uses the name of", Refused(listed, given).Message, StringComparison.Ordinal);
    }

    // ---- A repeated last page after a complete scan --------------------------------------------------------------------

    [Fact]
    public async Task A_repeated_page_after_every_ticket_was_read_isnt_truncation()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":2,"continuationToken":"t1"}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":2,"continuationToken":"t2"}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.Equal(2, r.Scanned);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task A_repeated_page_before_the_total_is_reached_is_truncation()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":3,"continuationToken":"t1"}""")
            .Enqueue(HttpStatusCode.OK, """{"items":[{"id":"1"},{"id":"2"}],"itemCount":3,"continuationToken":"t2"}""");

        TicketScan.Result<Ticket> r = await TicketScan.RunAsync(TestFactory.Client(handler), new TicketListQuery(), t => t, 100, 2, Ct);

        Assert.True(r.Truncated);
    }

    // ---- The follow-up's time limit covers a slow rate-limit queue ------------------------------------------------------

    [Fact]
    public void A_small_permit_count_lengthens_the_longest_request()
    {
        TicketingOptions few = TestFactory.Options(o => o.RateLimitPermits = 5);
        TicketingOptions many = TestFactory.Options();

        // 26 requests at the front of the queue take six windows at five permits a window.
        TimeSpan perAttempt = TimeSpan.FromSeconds((6 * few.RateLimitWindowSeconds) + few.RequestTimeoutSeconds);
        Assert.True(TicketingClient.LongestRequest(few) >= 3 * perAttempt);
        Assert.True(TicketingClient.LongestRequest(many) < TicketingClient.LongestRequest(few));
    }

    // ---- Configuration isn't read from the working directory ----------------------------------------------------------

    [Fact]
    public async Task A_settings_file_in_the_working_directory_is_ignored()
    {
        // MCP clients start a stdio server in the user's workspace, which a cloned repository or an agent can write to.
        // A settings file there that would stop startup (an unknown time zone) must not be read at all.
        using var workspace = new TempDir();
        File.WriteAllText(Path.Combine(workspace.Path, "appsettings.json"), """{"Ticketing":{"DefaultTimeZoneId":"Not/AZone","BaseUrl":"https://attacker.invalid/"}}""");

        string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in new[] { "exec", Path.Combine(AppContext.BaseDirectory, "TeamsWork.Ticketing.Mcp.dll"), "--stdio" })
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Ticketing__ApiKey"] = "test-key";
        start.Environment["Ticketing__ServiceAccount__Id"] = "44444444-4444-4444-4444-444444444444";
        start.Environment["Ticketing__ServiceAccount__Name"] = "Test";
        start.Environment["Ticketing__ServiceAccount__Email"] = "test@example.test";

        using Process server = Process.Start(start)!;
        try
        {
            Task<string> errors = server.StandardError.ReadToEndAsync(Ct);
            await server.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
            await server.StandardInput.FlushAsync(Ct);

            string? reply = await server.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.True(reply is not null, "The server exited: " + (server.HasExited ? await errors : ""));
            Assert.Contains("\"result\"", reply, StringComparison.Ordinal);
        }
        finally
        {
            // Waited for, since a process still running holds the working directory and the folder can't be deleted.
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_service_account_is_canonical_for_every_consumer()
    {
        await using var factory = new LocalModeFactory
        {
            Settings = { ["Ticketing:ServiceAccount:Id"] = "44444444-4444-4444-4444-44444444444A", ["Ticketing:ServiceAccount:Email"] = "Dev@EXAMPLE.test" },
        };

        ServiceAccountOptions sa = factory.Services.GetRequiredService<IOptions<TicketingOptions>>().Value.ServiceAccount!;

        Assert.Equal(("44444444-4444-4444-4444-44444444444a", "Dev@example.test"), (sa.Id, sa.Email));
        await Task.CompletedTask;
    }

    // ---- Upload folders on kernel pseudo-filesystems ------------------------------------------------------------------

    [Theory]
    [InlineData("/proc/self")]
    [InlineData("/sys/kernel")]
    [InlineData("/dev")]
    public void An_upload_folder_cant_be_a_kernel_pseudo_filesystem(string folder)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Directory.Exists(folder), "Linux pseudo-filesystems only.");

        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() =>
            UploadFolder.Create(TestFactory.Options(o => o.UploadRoot = folder), null));

        Assert.Contains("system folder", ex.Message, StringComparison.Ordinal);
    }

    // ---- Upstream requests in flight, and the error flag ----------------------------------------------------------------

    [Fact]
    public async Task Upstream_requests_in_flight_are_bounded()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(o => o.MaxConcurrentUpstreamRequests = 1));
        var handler = new ConcurrencyProbe();
        using var limiter = new TicketingRateLimiter(opts);
        var client = new TicketingClient(new HttpClient(handler), opts, limiter, new TimeZoneOffsetResolver(opts, TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.ListTicketsAsync(new TicketListQuery(), Ct)));

        Assert.Equal(4, handler.Calls);
        Assert.Equal(1, handler.MostAtOnce);
    }

    [Fact]
    public async Task An_error_flag_is_seen_whatever_type_the_message_is()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[],"error":true,"message":{"detail":"x"}}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.Contains("reported an error", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers after a short pause, recording the most requests it had at once.</summary>
    private sealed class ConcurrencyProbe : HttpMessageHandler
    {
        private int _now;
        private int _most;
        private int _calls;

        public int MostAtOnce => Volatile.Read(ref _most);

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            int now = Interlocked.Increment(ref _now);
            int most;
            while (now > (most = Volatile.Read(ref _most)) && Interlocked.CompareExchange(ref _most, now, most) != most)
            {
            }

            await Task.Delay(100, cancellationToken);
            Interlocked.Decrement(ref _now);
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[]}""");
        }
    }
}
