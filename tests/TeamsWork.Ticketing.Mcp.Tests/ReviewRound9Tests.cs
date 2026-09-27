using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the ninth review round.</summary>
public sealed class ReviewRound9Tests
{
    private const string TagsJson = """{"items":[{"id":"c1","text":"Hardware"}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The auth mode never comes from a settings file ---------------------------------------------------------------

    [Theory]
    [InlineData("Local")]
    [InlineData("secret-mode")] // nor can a file stop startup with a bad value
    public void A_settings_file_beside_the_executable_cant_choose_the_auth_mode(string value)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "appsettings.json"), $$$"""{"Auth":{"Mode":"{{{value}}}"}}""");
        IConfigurationBuilder builder = new ConfigurationBuilder().SetBasePath(dir.Path).AddJsonFile("appsettings.json");

        using ConfigurationRoot trusted = SettingsFiles.Without(builder);

        Assert.Equal(AuthMode.Entra, AuthModeResolver.Resolve(trusted, []));
    }

    [Fact]
    public void The_auth_mode_still_comes_from_the_other_sources()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "appsettings.json"), """{"Auth":{"Mode":"Entra"},"Other":"x"}""");
        IConfigurationBuilder builder = new ConfigurationBuilder()
            .SetBasePath(dir.Path)
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "Local" });

        using ConfigurationRoot trusted = SettingsFiles.Without(builder);

        Assert.Equal(AuthMode.Local, AuthModeResolver.Resolve(trusted, []));
        Assert.Null(trusted["Other"]); // the file isn't read at all
        Assert.Equal("x", builder.Build()["Other"]); // while the configuration itself keeps it, for the hosted server
    }

    // ---- An ordinary read joins a refresh in flight ------------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_instance_read_joins_a_refresh_at_the_same_offset()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        var handler = new HeldHandler(InstanceJson);
        var time = new MutableTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        TicketingClient client = ClientFor(handler);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time);

        Task<Instance> refreshing = Task.Run(() => cache.GetInstanceAsync(client, -5, refresh: true, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<Instance> ordinary = cache.GetInstanceAsync(client, -5, refresh: false, Ct);
        handler.Release.SetResult();

        await Task.WhenAll(refreshing, ordinary);
        Assert.Equal(1, handler.Count);
        Assert.Same(await refreshing, await ordinary);
    }

    [Fact]
    public async Task An_ordinary_instance_read_at_another_offset_doesnt_join_a_refresh()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        var handler = new HeldHandler(InstanceJson);
        var time = new MutableTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        TicketingClient client = ClientFor(handler);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time);

        Task<Instance> refreshing = Task.Run(() => cache.GetInstanceAsync(client, -5, refresh: true, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetInstanceAsync(client, -6, refresh: false, Ct); // its times are in another offset
        Assert.Equal(2, handler.Count);

        handler.Release.SetResult();
        await refreshing;
    }

    [Fact]
    public async Task An_ordinary_tag_read_joins_a_refresh()
    {
        IOptions<TicketingOptions> opts = Microsoft.Extensions.Options.Options.Create(TestFactory.Options());
        var handler = new HeldHandler(TagsJson);
        var time = new MutableTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        TicketingClient client = ClientFor(handler);
        var cache = new InstanceCache(opts, new TimeZoneOffsetResolver(opts, time), time);

        Task<IReadOnlyList<TagCategory>> refreshing = Task.Run(() => cache.GetTagCategoriesAsync(client, refresh: true, Ct), Ct);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<IReadOnlyList<TagCategory>> ordinary = cache.GetTagCategoriesAsync(client, refresh: false, Ct);
        handler.Release.SetResult();

        await Task.WhenAll(refreshing, ordinary);
        Assert.Equal(1, handler.Count);
        Assert.Equal("Hardware", Assert.Single(await ordinary).Text);
    }

    /// <summary>Answers with <paramref name="body"/>, holding the first request until released.</summary>
    private sealed class HeldHandler(string body) : HttpMessageHandler
    {
        private int _count;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, body);
        }
    }
}
