using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the eighth review round.</summary>
public sealed class ReviewRound8Tests
{
    // A root filesystem (8:1) with a bind of a protected folder, a second view of the whole filesystem, a separate disk,
    // and a bind whose paths contain an escaped space.
    private const string MountTable =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "30 22 8:1 /home/u/.config /srv/uploads rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "31 22 8:1 / /mnt/all rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "32 22 8:2 / /data rw,relatime shared:2 - ext4 /dev/sdb1 rw\n" +
        "33 22 8:1 /home/u/my\\040files /srv/with\\040space rw - ext4 /dev/sda1 rw\n" +
        "40 22 0:5 / /host/proc rw,nosuid shared:9 - proc proc rw\n";

    private const string Outsider = "22222222-2222-2222-2222-222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_bind_mounted_folder_is_seen_at_its_real_location()
    {
        IReadOnlyList<string> aliases = UploadFolder.MountAliases(MountTable, "/srv/uploads/app", 30)!.Aliases;

        Assert.Contains("/home/u/.config/app", aliases);   // what the protected-folder checks must see
        Assert.Contains("/mnt/all/home/u/.config/app", aliases);
        Assert.DoesNotContain("/srv/uploads/app", aliases);
    }

    [Fact]
    public void A_second_view_of_a_whole_filesystem_is_seen_at_its_usual_path()
    {
        Assert.Contains("/home/u/.config", UploadFolder.MountAliases(MountTable, "/mnt/all/home/u/.config", 31)!.Aliases);
    }

    [Fact]
    public void A_folder_on_a_filesystem_mounted_once_has_no_aliases()
    {
        Assert.Empty(UploadFolder.MountAliases(MountTable, "/data/uploads", 32)!.Aliases);
    }

    [Fact]
    public void Escaped_characters_in_the_mount_table_are_decoded()
    {
        Assert.Contains("/home/u/my files/x", UploadFolder.MountAliases(MountTable, "/srv/with space/x", 33)!.Aliases);
    }

    [Theory]
    [InlineData("/srv/uploads/app", 99)] // a mount the table doesn't list
    [InlineData("/elsewhere/app", 30)]   // a path not under that mount's mount point
    public void A_mount_that_cant_be_matched_gives_no_answer(string path, ulong mount)
    {
        Assert.Null(UploadFolder.MountAliases(MountTable, path, mount));
    }

    [Fact]
    public void A_folder_reports_the_type_of_filesystem_it_is_on()
    {
        Assert.Equal("proc", UploadFolder.MountAliases(MountTable, "/host/proc/1", 40)!.FileSystemType);
        Assert.Equal("ext4", UploadFolder.MountAliases(MountTable, "/data/uploads", 32)!.FileSystemType);
    }

    // ---- Look-alikes of I and m, and case in every script ----------------------------------------------------------------

    private static Instance ListedAs(string name) => JsonSerializer.Deserialize<ItemResponse<Instance>>(
        "{\"item\":{\"id\":\"i\",\"assignees\":{\"peoples\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"" + name +
        "\",\"email\":\"listed@contoso.com\"}]}}}",
        TicketingClient.JsonOptions)!.Item!;

    private static TicketUser Resolve(string listed, string given) =>
        InstanceLookup.ResolvePerson(new UserRef(Outsider, given, "pat@outside.test"), "requestor", ListedAs(listed), assigneeOnly: false);

    [Theory]
    [InlineData("Ivan Petrov", "\u0399van Petrov")]      // Greek capital iota
    [InlineData("Ivan Petrov", "\u0406van Petrov")]      // Cyrillic capital I
    [InlineData("Ivan Petrov", "\uFF29van Petrov")]      // full-width I
    [InlineData("Ivan Petrov", "\U0001D408van Petrov")]  // mathematical bold I
    [InlineData("Alice Smith", "AIice Smith")]            // plain capital I for l
    [InlineData("Emma Stone", "E\uFF4D\uFF4Da Stone")]  // full-width m
    [InlineData("Emma Stone", "Ernrna Stone")]            // rn for m
    [InlineData("John Doe", "J0hn Doe")]                  // zero for o
    [InlineData("Lin Wei", "\u04C0in Wei")]              // Cyrillic capital palochka
    [InlineData("N\u03AF\u03BA\u03BF\u03C2", "\u039D\u0399\u039A\u039F\u03A3")] // Greek in capitals
    [InlineData("\u041D\u0438\u043D\u0430", "\u041D\u0418\u041D\u0410")]   // Cyrillic in capitals
    [InlineData("Jane Doe", "jane doe")]
    public void Look_alikes_and_case_variants_of_a_listed_name_are_the_same_name(string listed, string given)
    {
        Assert.Contains("uses the name of", Assert.Throws<McpException>(() => Resolve(listed, given)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hana Kim", "Nina Kim")]   // Greek capital Eta looks like H and small eta like n: nothing chains them
    [InlineData("Ivan Petrov", "Evan Petrov")]
    [InlineData("Mia Lopez", "Mila Lopez")]
    public void Different_names_arent_merged_through_case_or_script(string listed, string given)
    {
        Assert.Equal(given, Resolve(listed, given).Name);
    }

    // ---- Upstream leases: slots, permits and backoff ----------------------------------------------------------------

    private static (TicketingClient Client, TicketingRateLimiter Limiter) ClientWith(HttpMessageHandler handler, Action<TicketingOptions> configure)
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options(configure));
        var limiter = new TicketingRateLimiter(opts);
        return (new TicketingClient(new HttpClient(handler), opts, limiter, new TimeZoneOffsetResolver(opts, TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketingClient>.Instance, TimeProvider.System), limiter);
    }

    [Fact]
    public async Task A_retry_backoff_holds_no_in_flight_slot()
    {
        var handler = new OrderedHandler();
        (TicketingClient client, TicketingRateLimiter limiter) = ClientWith(handler, o => o.MaxConcurrentUpstreamRequests = 1);
        using (limiter)
        {
            // A is told to retry after a second; B, started meanwhile, goes out during that second rather than after it.
            Task first = client.ListTicketsAsync(new TicketListQuery { Search = "a" }, Ct);
            await handler.FirstAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Task second = client.ListTicketsAsync(new TicketListQuery { Search = "b" }, Ct);
            await Task.WhenAll(first, second);
        }

        Assert.Equal(["a", "b", "a"], handler.Order);
    }

    [Fact]
    public async Task A_request_refused_as_busy_spends_no_rate_permit()
    {
        var handler = new HeldHandler("""{"items":[]}""");
        (TicketingClient client, TicketingRateLimiter limiter) = ClientWith(handler, o =>
        {
            o.MaxConcurrentUpstreamRequests = 1;
            o.RateLimitPermits = 40;
        });
        using (limiter)
        {
            // One in flight and 32 waiting: of 40 at once, 7 are refused as busy before taking a permit.
            // The first is held until all 40 have started, so none of the waiting ones can go out early.
            Task[] burst = Enumerable.Range(0, 40).Select(_ => (Task)client.ListTicketsAsync(new TicketListQuery(), Ct)).ToArray();
            handler.Release.SetResult();
            try
            {
                await Task.WhenAll(burst);
            }
            catch (TicketingApiException)
            {
            }

            Assert.Equal(7, burst.Count(t => t.IsFaulted));

            // So 7 of the 40 permits are left, and the next request goes out at once instead of waiting for the window.
            await client.ListTicketsAsync(new TicketListQuery(), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        Assert.Equal(34, handler.Count);
    }

    // ---- The local modes read no settings files ------------------------------------------------------------------

    [Fact]
    public async Task Local_mode_reads_no_settings_files_and_keeps_the_logging_defaults()
    {
        await using var factory = new LocalModeFactory();
        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        Assert.DoesNotContain(configuration.Providers, p => p is JsonConfigurationProvider json &&
            json.Source.Path?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("None", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
    }

    /// <summary>Records the order of searches; the first "a" is answered 429 with a one-second Retry-After.</summary>
    private sealed class OrderedHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private bool _refused;

        public List<string> Order { get; } = [];

        public TaskCompletionSource FirstAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string search = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["search"] ?? "";
            lock (_lock)
            {
                Order.Add(search);
                if (search == "a" && !_refused)
                {
                    _refused = true;
                    var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    FirstAnswered.SetResult();
                    return Task.FromResult(tooMany);
                }
            }

            return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[]}"""));
        }
    }
}
