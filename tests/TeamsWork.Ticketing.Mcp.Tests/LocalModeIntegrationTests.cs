using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Hosts the HTTP transport in local (unauthenticated, loopback-only) mode.</summary>
public sealed class LocalModeFactory : WebApplicationFactory<Program>
{
    public bool IncludeServiceAccount { get; init; } = true;

    /// <summary>Extra settings, applied last.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];

    /// <summary>Service replacements, applied after the app's own registrations.</summary>
    public Action<IServiceCollection>? ReplaceServices { get; init; }

    /// <summary>A JSON settings file, added last, for values only JSON can express (such as null).</summary>
    public string? Json { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (ReplaceServices is not null)
        {
            builder.ConfigureTestServices(ReplaceServices);
        }

        if (Json is not null)
        {
            builder.ConfigureAppConfiguration(c => c.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json))));
        }

        builder.UseEnvironment("Production");
        builder.UseSetting("Auth:Mode", "Local");
        builder.UseSetting("Ticketing:ApiKey", "local-test-key");
        builder.UseSetting("Ticketing:BaseUrl", "https://ticketing.invalid/v1");

        // Set even when excluded, so Ticketing__ServiceAccount__* variables on a developer machine cannot fill them in.
        builder.UseSetting("Ticketing:ServiceAccount:Id", IncludeServiceAccount ? "44444444-4444-4444-4444-444444444444" : "");
        builder.UseSetting("Ticketing:ServiceAccount:Name", IncludeServiceAccount ? "Local Dev" : "");
        builder.UseSetting("Ticketing:ServiceAccount:Email", IncludeServiceAccount ? "dev@example.test" : "");

        foreach ((string key, string value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

public sealed class LocalModeIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Local_mode_serves_tools_without_a_token()
    {
        await using var factory = new LocalModeFactory();
        HttpClient http = factory.CreateClient();

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "local" },
            http, loggerFactory: null, ownsHttpClient: true);
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);
        // Every tool but upload_ticket_files, which only the stdio transport offers.
        Assert.Equal(20, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "upload_ticket_files");

        // Validation still runs and the configured account is what writes would be attributed to.
        CallToolResult result = await client.CallToolAsync("get_ticket", new Dictionary<string, object?> { ["ticketId"] = "nope" }, cancellationToken: Ct);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Local_mode_healthz_is_available()
    {
        await using var factory = new LocalModeFactory();
        using HttpClient http = factory.CreateClient();

        using HttpResponseMessage r = await http.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Local_mode_does_not_publish_entra_metadata()
    {
        await using var factory = new LocalModeFactory();
        using HttpClient http = factory.CreateClient();

        using HttpResponseMessage r = await http.GetAsync("/.well-known/oauth-protected-resource", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("attacker.example")]         // DNS rebinding: page's own hostname pointed at 127.0.0.1
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("localhost:9999")]           // right host, wrong port
    public async Task Local_mode_rejects_foreign_host_headers(string host)
    {
        await using var factory = new LocalModeFactory();
        using HttpClient http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = ToolsList() };
        request.Headers.Host = host;

        using HttpResponseMessage r = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("null")]
    [InlineData("http://localhost:9999")]
    public async Task Local_mode_rejects_cross_origin_requests(string origin)
    {
        await using var factory = new LocalModeFactory();
        using HttpClient http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = ToolsList() };
        request.Headers.TryAddWithoutValidation("Origin", origin);

        using HttpResponseMessage r = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Local_mode_accepts_same_machine_origin()
    {
        await using var factory = new LocalModeFactory();
        using HttpClient http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = ToolsList() };
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using HttpResponseMessage r = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static StringContent ToolsList() =>
        new("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Unexpected_failures_reach_the_client_without_their_details()
    {
        // An exception no tool code anticipates, carrying something that must not leak.
        await using var factory = new LocalModeFactory
        {
            ReplaceServices = s => s.AddHttpClient<TeamsWork.Ticketing.Mcp.Ticketing.TicketingClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler(new InvalidOperationException("secret-detail-7f3a"))),
        };
        HttpClient http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "local" },
            http, loggerFactory: null, ownsHttpClient: true);
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        CallToolResult result = await client.CallToolAsync("list_tag_categories", cancellationToken: Ct);

        Assert.True(result.IsError);
        string text = string.Join(' ', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.NotEmpty(text);
        Assert.DoesNotContain("secret-detail-7f3a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    [Fact]
    public async Task Local_mode_refuses_to_start_without_a_service_account()
    {
        await using var factory = new LocalModeFactory { IncludeServiceAccount = false };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ServiceAccount", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_mode_refuses_to_start_with_an_unknown_time_zone()
    {
        await using var factory = new LocalModeFactory { Settings = { ["Ticketing:DefaultTimeZoneId"] = "Not/A_Zone" } };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Ticketing:DefaultTimeZoneId", ex.ToString(), StringComparison.Ordinal);
    }

    // .NET 10 binds an explicit JSON null, so a user-secrets file with "Ticketing:DefaultTimeZoneId": null reaches the
    // validators as null.
    [Fact]
    public async Task A_null_time_zone_is_reported_as_a_configuration_problem()
    {
        await using var factory = new LocalModeFactory { Json = """{ "Ticketing": { "DefaultTimeZoneId": null } }""" };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains("Ticketing:DefaultTimeZoneId", report, StringComparison.Ordinal);
    }

    // Seen live: an ID that isn't an object ID attributed every write to someone the help desk didn't know.
    [Theory]
    [InlineData("not-an-object-id", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)] // parses as a GUID, but is no one's object ID
    [InlineData("dev@example.test", true)] // the email-to-ticket form
    [InlineData("44444444-4444-4444-4444-444444444444", true)]
    public async Task Service_account_id_must_be_an_object_id_or_the_email(string id, bool starts)
    {
        await using var factory = new LocalModeFactory { Settings = { ["Ticketing:ServiceAccount:Id"] = id } };

        if (starts)
        {
            factory.CreateClient().Dispose();
            return;
        }

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains("Entra object ID", report, StringComparison.Ordinal);
        Assert.DoesNotContain(id, report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EU", "https://ticketing-apim-eu.azure-api.net/ticketing/v1")]
    [InlineData("aus", "https://ticketing-apim-aus.azure-api.net/ticketing/v1")]
    [InlineData("US", TicketingOptions.DefaultBaseUrl)]
    public async Task Region_picks_the_vendor_endpoint(string region, string expected)
    {
        await using var factory = new LocalModeFactory
        {
            Settings = { ["Ticketing:BaseUrl"] = TicketingOptions.DefaultBaseUrl, ["Ticketing:Region"] = region },
        };
        factory.CreateClient().Dispose();

        TicketingOptions options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TicketingOptions>>().Value;

        Assert.Equal(expected, options.BaseUrl);
    }

    [Fact]
    public async Task No_region_keeps_the_us_endpoint()
    {
        await using var factory = new LocalModeFactory { Settings = { ["Ticketing:BaseUrl"] = TicketingOptions.DefaultBaseUrl } };
        factory.CreateClient().Dispose();

        TicketingOptions options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TicketingOptions>>().Value;

        Assert.Equal(TicketingOptions.DefaultBaseUrl, options.BaseUrl);
    }

    [Theory]
    [InlineData("Mars", "Ticketing:Region must be one of")]
    [InlineData("EU", "name different endpoints")] // the factory's BaseUrl is a custom one
    public async Task Bad_or_conflicting_region_stops_startup(string region, string message)
    {
        await using var factory = new LocalModeFactory { Settings = { ["Ticketing:Region"] = region } };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains(message, report, StringComparison.Ordinal);
    }

    // Each bad value must reach the entry point as a configuration failure (reported without a stack trace) that
    // names the setting but never repeats the value.
    [Theory]
    [InlineData("Local:Port", "secret-port")]
    [InlineData("Ticketing:MaxPageSize", "secret-size")]
    [InlineData("KeyVault:Uri", "secret-vault")]
    [InlineData("Auth:Mode", "secret-mode")]
    public async Task Bad_setting_values_are_reported_as_configuration_problems(string key, string value)
    {
        await using var factory = new LocalModeFactory { Settings = { [key] = value } };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains(key, report, StringComparison.Ordinal);
        Assert.DoesNotContain(value, report, StringComparison.Ordinal);
    }
}
