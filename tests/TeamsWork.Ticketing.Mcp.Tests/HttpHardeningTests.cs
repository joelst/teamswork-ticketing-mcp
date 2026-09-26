using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Protections on the Entra-protected HTTP endpoint: what a caller, authenticated or not, can make it do.</summary>
public sealed class HttpHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TokenFor(string oid) => McpServerFactory.CreateToken(new Dictionary<string, object>
    {
        ["scp"] = "access_as_user",
        ["oid"] = oid,
        ["name"] = oid,
        ["preferred_username"] = $"{oid}@example.test",
        ["tid"] = McpServerFactory.TenantId,
    });

    private static HttpRequestMessage ToolsList(string token, string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body ?? """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task Resource_metadata_ignores_a_client_supplied_forwarded_host()
    {
        await using var factory = new McpServerFactory();
        using HttpClient http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.example");

        using HttpResponseMessage r = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("attacker.example", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    [InlineData("Common")]
    public async Task Multi_tenant_authorities_are_refused(string tenant)
    {
        await using var factory = new McpServerFactory { Settings = { ["Entra:TenantId"] = tenant } };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains("Entra:TenantId", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_caller_is_rate_limited_without_affecting_another()
    {
        await using var factory = new McpServerFactory { Settings = { ["Mcp:RequestsPerMinutePerCaller"] = "3" } };
        using HttpClient http = factory.CreateClient();
        string alice = TokenFor("alice-oid"), bob = TokenFor("bob-oid");

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage ok = await http.SendAsync(ToolsList(alice), Ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using HttpResponseMessage limited = await http.SendAsync(ToolsList(alice), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        using HttpResponseMessage other = await http.SendAsync(ToolsList(bob), Ct);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Json_rpc_batches_are_refused()
    {
        // The per-caller limit counts HTTP requests. That only bounds tool calls while one request carries one call:
        // if the SDK ever accepts batches, the limiter has to count messages instead.
        await using var factory = new McpServerFactory();
        using HttpClient http = factory.CreateClient();
        string batch = "[" + string.Join(',', Enumerable.Range(1, 5).Select(i =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"tools/list\",\"params\":{}}")) + "]";

        using HttpResponseMessage r = await http.SendAsync(ToolsList(TokenFor("alice-oid"), batch), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Oversized_request_bodies_are_refused()
    {
        await using var factory = new McpServerFactory();
        using HttpClient http = factory.CreateClient();
        // A well-formed JSON-RPC request, padded past the limit.
        string padding = new('x', 2 * 1024 * 1024);
        string body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { pad = padding } });

        using HttpResponseMessage r = await http.SendAsync(ToolsList(TokenFor("alice-oid"), body), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    // A chunked body declares no length, so only the server's MaxRequestBodySize stops it, and the in-process test host
    // doesn't enforce that. This runs the real Kestrel server.
    [Fact]
    public async Task Oversized_chunked_request_bodies_are_refused_by_kestrel()
    {
        await using var factory = new McpServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        // A plain client, so nothing in the factory's handler chain can buffer the body and give it a length.
        using HttpClient factoryClient = factory.CreateClient();
        using var http = new HttpClient { BaseAddress = factoryClient.BaseAddress };
        using HttpRequestMessage request = ToolsList(TokenFor("alice-oid"));
        request.Headers.TransferEncodingChunked = true;
        request.Content = new UnsizedContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"pad\":\"", new string('x', 2 * 1024 * 1024), "\"}}");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage r = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    /// <summary>Content that reports no length, so HttpClient sends it with chunked transfer encoding.</summary>
    private sealed class UnsizedContent(params string[] parts) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (string part in parts)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(part));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Normal_sized_requests_still_work()
    {
        await using var factory = new McpServerFactory();
        using HttpClient http = factory.CreateClient();
        string body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { pad = new string('x', 100_000) } });

        using HttpResponseMessage r = await http.SendAsync(ToolsList(TokenFor("alice-oid"), body), Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
