using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Hosts the HTTP transport in local (unauthenticated, loopback-only) mode.</summary>
public sealed class LocalModeFactory : WebApplicationFactory<Program>
{
    public bool IncludeServiceAccount { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Auth:Mode", "Local");
        builder.UseSetting("Ticketing:ApiKey", "local-test-key");
        builder.UseSetting("Ticketing:BaseUrl", "https://ticketing.invalid/v1");

        if (IncludeServiceAccount)
        {
            builder.UseSetting("Ticketing:ServiceAccount:Id", "local-oid");
            builder.UseSetting("Ticketing:ServiceAccount:Name", "Local Dev");
            builder.UseSetting("Ticketing:ServiceAccount:Email", "dev@example.test");
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
        Assert.Equal(12, tools.Count);

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
    public async Task Local_mode_refuses_to_start_without_a_service_account()
    {
        await using var factory = new LocalModeFactory { IncludeServiceAccount = false };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ServiceAccount", ex.ToString(), StringComparison.Ordinal);
    }
}
