using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>
/// Hosts the real HTTP pipeline (Entra JWT validation, MCP challenge, authorization policy, MCP endpoint) and drives
/// it with tokens signed by a test key so no network access to Entra is needed.
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string ClientId = "22222222-2222-2222-2222-222222222222";
    public static readonly string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
    public static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(64));

    /// <summary>Extra settings, applied last.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Entra:TenantId", TenantId);
        builder.UseSetting("Entra:ClientId", ClientId);
        builder.UseSetting("Ticketing:ApiKey", "integration-test-key");
        builder.UseSetting("Ticketing:BaseUrl", "https://ticketing.invalid/v1");
        builder.UseSetting("Ticketing:ServiceAccount:Id", "sa-oid");
        builder.UseSetting("Ticketing:ServiceAccount:Name", "Ticketing Bot");
        builder.UseSetting("Ticketing:ServiceAccount:Email", "bot@example.test");

        foreach ((string key, string value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Replace Entra metadata discovery with a static configuration and our test signing key.
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration());
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudiences = [ClientId, $"api://{ClientId}"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = SigningKey,
                    NameClaimType = "name",
                    RoleClaimType = "roles",
                };
            });
        });
    }

    public static string CreateToken(IDictionary<string, object> claims, string? audience = null)
    {
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? $"api://{ClientId}",
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        });
    }

    public static string UserToken(string scope = "access_as_user") => CreateToken(new Dictionary<string, object>
    {
        ["scp"] = scope,
        ["oid"] = "user-oid-1",
        ["name"] = "Pat Example",
        ["preferred_username"] = "pat@example.test",
        ["tid"] = TenantId,
    });

    public static string AppToken(string role = "Ticketing.ReadWrite") => CreateToken(new Dictionary<string, object>
    {
        ["roles"] = new[] { role },
        ["oid"] = "sp-oid-1",
        ["tid"] = TenantId,
    });
}

public sealed class AuthIntegrationTests : IClassFixture<McpServerFactory>
{
    private static readonly string[] ExpectedTools =
    [
        "list_tickets", "get_ticket", "create_ticket", "update_ticket", "update_ticket_status",
        "list_ticket_activities", "add_ticket_comment",
        "list_ticket_attachments", "add_ticket_link_attachments", "list_activity_attachments",
        "get_instance", "list_tag_categories",
    ];

    private readonly McpServerFactory _factory;

    public AuthIntegrationTests(McpServerFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Entra:Instance", "secret-instance")]
    [InlineData("Entra:PublicBaseUrl", "secret-base")]
    public async Task Bad_entra_urls_are_reported_as_configuration_problems(string key, string value)
    {
        await using var factory = new McpServerFactory { Settings = { [key] = value } };

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(StartupErrorReport.TryFormat(ex, new System.Collections.Hashtable(), null, out string report), ex.ToString());
        Assert.Contains(key, report, StringComparison.Ordinal);
        Assert.DoesNotContain(value, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthz_is_anonymous()
    {
        using HttpClient http = _factory.CreateClient();
        using HttpResponseMessage r = await http.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Mcp_without_token_returns_401_with_resource_metadata_challenge()
    {
        using HttpClient http = _factory.CreateClient();
        using HttpResponseMessage r = await http.PostAsync("/mcp", JsonRpc("tools/list"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        AuthenticationHeaderValue challenge = Assert.Single(r.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Contains("resource_metadata=", challenge.Parameter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protected_resource_metadata_is_published_for_this_host()
    {
        using HttpClient http = _factory.CreateClient();
        using HttpResponseMessage challengeResponse = await http.PostAsync("/mcp", JsonRpc("tools/list"), Ct);
        string parameter = challengeResponse.Headers.WwwAuthenticate.First(h => h.Scheme == "Bearer").Parameter!;
        string metadataUrl = Regex.Match(parameter, "resource_metadata=\"?([^\",]+)").Groups[1].Value;

        using HttpResponseMessage r = await http.GetAsync(metadataUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        JsonElement root = doc.RootElement;
        Assert.EndsWith("/mcp", root.GetProperty("resource").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(http.BaseAddress!.ToString().TrimEnd('/'), root.GetProperty("resource").GetString(), StringComparison.Ordinal);
        Assert.Contains(McpServerFactory.Issuer, root.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains($"api://{McpServerFactory.ClientId}/access_as_user", root.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Token_for_another_audience_is_rejected()
    {
        using HttpClient http = _factory.CreateClient();
        string token = McpServerFactory.CreateToken(new Dictionary<string, object> { ["scp"] = "access_as_user", ["oid"] = "x" }, audience: "api://someone-else");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage r = await http.PostAsync("/mcp", JsonRpc("tools/list"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Token_without_required_scope_or_role_is_forbidden()
    {
        using HttpClient http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpServerFactory.UserToken(scope: "something_else"));

        using HttpResponseMessage r = await http.PostAsync("/mcp", JsonRpc("tools/list"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Delegated_token_can_list_all_tools()
    {
        await using McpClient client = await ConnectAsync(McpServerFactory.UserToken());

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());
        McpClientTool create = tools.Single(t => t.Name == "create_ticket");
        Assert.False(create.ProtocolTool.Annotations?.ReadOnlyHint);
        McpClientTool list = tools.Single(t => t.Name == "list_tickets");
        Assert.True(list.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task App_only_token_with_role_is_accepted()
    {
        await using McpClient client = await ConnectAsync(McpServerFactory.AppToken());

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(ExpectedTools.Length, tools.Count);
    }

    [Fact]
    public async Task Tool_argument_validation_errors_reach_the_client()
    {
        await using McpClient client = await ConnectAsync(McpServerFactory.UserToken());

        CallToolResult result = await client.CallToolAsync(
            "get_ticket",
            new Dictionary<string, object?> { ["ticketId"] = "1839" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("ticketId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("integration-test-key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_tools_never_expose_a_user_parameter()
    {
        await using McpClient client = await ConnectAsync(McpServerFactory.UserToken());

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);

        foreach (McpClientTool tool in tools)
        {
            JsonElement schema = tool.ProtocolTool.InputSchema;
            if (schema.TryGetProperty("properties", out JsonElement props))
            {
                Assert.False(props.TryGetProperty("user", out _), $"{tool.Name} must not accept a 'user' argument");
            }
        }
    }

    private async Task<McpClient> ConnectAsync(string bearerToken)
    {
        HttpClient http = _factory.CreateClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"),
            Name = "test",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" },
        };
        var transport = new HttpClientTransport(options, http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static StringContent JsonRpc(string method) =>
        new($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{method}}}","params":{}}""", System.Text.Encoding.UTF8, "application/json");
}
