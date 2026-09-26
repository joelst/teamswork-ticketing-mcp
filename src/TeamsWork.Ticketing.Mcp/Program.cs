using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Tools;

// ---------------------------------------------------------------------------------------------------------------
// Entry point. One binary, three ways to run it:
//   * default                        -> HTTP at /mcp with Entra ID bearer auth (Foundry, Copilot Studio, VS Code)
//   * --local / Auth:Mode=Local      -> HTTP at http://127.0.0.1:<Local:Port>/mcp, no auth, loopback only,
//                                       Ticketing:ServiceAccount required (local development)
//   * --stdio / MCP_TRANSPORT=stdio  -> stdio for local clients (Claude Code, VS Code), Ticketing:ServiceAccount required
// ---------------------------------------------------------------------------------------------------------------

bool useStdio = args.Contains("--stdio", StringComparer.OrdinalIgnoreCase) ||
                string.Equals(Environment.GetEnvironmentVariable("MCP_TRANSPORT"), "stdio", StringComparison.OrdinalIgnoreCase);

// A configuration problem that ends the process gets a plain explanation on stderr (never stdout, the stdio MCP
// channel) instead of the runtime's stack trace. Hooking the unhandled-exception event, rather than catching here,
// leaves the exception untouched for hosts that run this entry point in-process, such as the integration tests.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    // Only the local modes read user secrets, so only point there when running one of them.
    if (e.ExceptionObject is Exception ex &&
        StartupErrorReport.TryFormat(ex, Environment.GetEnvironmentVariables(), Program.UserSecretsLoaded ? UserSecretsFilePath() : null, out string report))
    {
        Console.Error.WriteLine(report);
        Console.Error.Flush();
        Environment.Exit(1);
    }
};

if (useStdio)
{
    await RunStdioAsync(args);
}
else
{
    await RunHttpAsync(args);
}

// ---------------------------------------------------------------------------------------------------------------

static async Task RunStdioAsync(string[] args)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

    // stdout is the MCP channel; every log line must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    // User secrets are handy for the API key locally; load them in every environment for this transport.
    UserSecretsConfiguration.Add(builder.Configuration, Assembly.GetExecutingAssembly());
    Program.UserSecretsLoaded = true;
    AddKeyVaultIfConfigured(builder.Configuration);
    AddTicketingServices(builder.Services, builder.Configuration, requireServiceAccount: true);
    builder.Services.AddSingleton<IActingUserProvider, ServiceAccountActingUserProvider>();

    builder.Services
        .AddMcpServer(ConfigureServerOptions)
        .WithStdioServerTransport()
        .WithTools<TicketTools>()
        .WithTools<ActivityTools>()
        .WithTools<AttachmentTools>()
        .WithTools<InstanceTools>();

    IHost host = builder.Build();
    ActingUser actor = await host.Services.GetRequiredService<IActingUserProvider>().GetActingUserAsync(CancellationToken.None);
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
        .LogInformation("stdio transport; ticket changes will be attributed to {Name} <{Email}>.", actor.Name, actor.Email);

    await host.RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    AuthMode authMode = AuthModeResolver.Resolve(builder.Configuration, args);

    if (authMode == AuthMode.Local)
    {
        UserSecretsConfiguration.Add(builder.Configuration, Assembly.GetExecutingAssembly());
        Program.UserSecretsLoaded = true;
    }

    AddKeyVaultIfConfigured(builder.Configuration);
    AddTicketingServices(builder.Services, builder.Configuration, requireServiceAccount: authMode == AuthMode.Local);
    builder.Services.AddHealthChecks();

    int localPort = 0;
    EntraOptions entra = new();

    if (authMode == AuthMode.Local)
    {
        localPort = ConfigureLocalMode(builder);
    }
    else
    {
        entra = ConfigureEntraMode(builder);
    }

    // --- MCP server --------------------------------------------------------------------------------------------
    IMcpServerBuilder mcp = builder.Services
        .AddMcpServer(ConfigureServerOptions)
        .WithHttpTransport(o =>
        {
            // Stateless is required by Foundry and removes session-hijack surface (no Mcp-Session-Id to steal).
            o.SessionMode = HttpServerSessionMode.Stateless;
        })
        .WithTools<TicketTools>()
        .WithTools<ActivityTools>()
        .WithTools<AttachmentTools>()
        .WithTools<InstanceTools>();

    if (authMode == AuthMode.Entra)
    {
        mcp.AddAuthorizationFilters();
    }

    WebApplication app = builder.Build();

    if (authMode == AuthMode.Local)
    {
        app.Use((ctx, next) => RejectNonLocalAsync(ctx, next, localPort));
        app.MapHealthChecks("/healthz");
        app.MapMcp("/mcp");

        ActingUser actor = await app.Services.GetRequiredService<IActingUserProvider>().GetActingUserAsync(CancellationToken.None);
        app.Logger.LogWarning(
            "LOCAL MODE: no authentication. Listening on http://127.0.0.1:{Port}/mcp (loopback only). Ticket changes are attributed to {Name} <{Email}>.",
            localPort, actor.Name, actor.Email);
    }
    else
    {
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/healthz").AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization("McpCaller");
        app.Logger.LogInformation("Entra authentication enabled for tenant {TenantId}, audience {Audience}.", entra.TenantId, entra.ApplicationIdUri);
    }

    await app.RunAsync();
}

// ---------------------------------------------------------------------------------------------------------------
// Local mode: unauthenticated, loopback only, explicit acting account.
// ---------------------------------------------------------------------------------------------------------------

static int ConfigureLocalMode(WebApplicationBuilder builder)
{
    // Refuse to run unauthenticated inside Azure Container Apps (the platform sets CONTAINER_APP_NAME).
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")))
    {
        throw new StartupConfigurationException("Auth:Mode=Local is for developer machines only and cannot be used in Azure Container Apps.");
    }

    int port = StartupConfigurationException.ReadSetting(() => builder.Configuration.GetValue<int?>("Local:Port"), "Local:Port") ?? 5188;
    if (port is < 1 or > 65535)
    {
        throw new StartupConfigurationException("Local:Port must be between 1 and 65535.");
    }

    // Bind to loopback only, regardless of ASPNETCORE_URLS or launch settings.
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Services.AddSingleton<IActingUserProvider, ServiceAccountActingUserProvider>();
    return port;
}

/// <summary>
/// Local mode is an unauthenticated endpoint that can write tickets, so it must not be reachable from anything but
/// local tools. Three checks:
/// <list type="bullet">
///   <item>the TCP peer is a loopback address;</item>
///   <item>the Host header names this machine, which defeats DNS rebinding (a web page whose hostname has been
///   re-pointed at 127.0.0.1 still sends its own hostname);</item>
///   <item>any Origin header is a loopback origin, which stops cross-site POSTs from a browser (the MCP spec
///   requires local servers to validate Origin).</item>
/// </list>
/// </summary>
static async Task RejectNonLocalAsync(HttpContext context, RequestDelegate next, int port)
{
    // RemoteIpAddress is null only for the in-process test host; Kestrel always populates it.
    IPAddress? remote = context.Connection.RemoteIpAddress;
    string? reason = null;

    if (remote is not null && !IPAddress.IsLoopback(remote))
    {
        reason = "Local mode accepts loopback connections only.";
    }
    else if (!IsLocalHost(context.Request.Host.Host, context.Request.Host.Port, port))
    {
        reason = "Local mode only accepts requests addressed to 127.0.0.1 or localhost.";
    }
    else if (context.Request.Headers.Origin is { Count: > 0 } origin && !IsLocalOrigin(origin.ToString(), port))
    {
        reason = "Cross-origin requests are not allowed in local mode.";
    }

    if (reason is not null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync(reason);
        return;
    }

    await next(context);
}

static bool IsLocalHost(string host, int? requestPort, int port)
{
    bool hostOk = host is "127.0.0.1" or "[::1]" or "::1" ||
                  string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    // The in-process test host uses "localhost" with no port; Kestrel requests always carry the bound port.
    return hostOk && (requestPort is null || requestPort == port);
}

static bool IsLocalOrigin(string origin, int port) =>
    Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) &&
    uri.Scheme is "http" or "https" &&
    IsLocalHost(uri.Host, uri.IsDefaultPort ? null : uri.Port, port);

// ---------------------------------------------------------------------------------------------------------------
// Entra mode: JWT bearer validation + MCP protected-resource metadata / challenge.
// ---------------------------------------------------------------------------------------------------------------

static EntraOptions ConfigureEntraMode(WebApplicationBuilder builder)
{
    EntraOptions entra = builder.Configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>() ?? new EntraOptions();
    if (!entra.IsConfigured)
    {
        throw new StartupConfigurationException(
            "Entra:TenantId and Entra:ClientId must be configured. The HTTP endpoint always requires Microsoft Entra ID " +
            "authentication unless you start it with --local (loopback-only developer mode).");
    }

    // Both only matter once a token arrives, so without these checks a typo would surface as a failed request.
    if (!Uri.TryCreate(entra.Instance, UriKind.Absolute, out Uri? instance) || instance.Scheme != Uri.UriSchemeHttps)
    {
        throw new StartupConfigurationException("Entra:Instance must be an absolute https URL. Leave it unset to use https://login.microsoftonline.com/.");
    }

    if (!string.IsNullOrWhiteSpace(entra.PublicBaseUrl) &&
        !(Uri.TryCreate(entra.PublicBaseUrl, UriKind.Absolute, out Uri? publicBase) && publicBase.Scheme is "http" or "https"))
    {
        throw new StartupConfigurationException("Entra:PublicBaseUrl must be an absolute http or https URL, or left unset.");
    }

    builder.Services.AddOptions<EntraOptions>().Bind(builder.Configuration.GetSection(EntraOptions.SectionName));
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IActingUserProvider, HttpActingUserProvider>();

    // Container Apps ingress terminates TLS; honour its forwarded headers so scheme/host are the public ones.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    AuthenticationBuilder auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    });

    // Validates issuer (tenant), audience (clientId and api://clientId), lifetime and signature against Entra keys.
    auth.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection(EntraOptions.SectionName), JwtBearerDefaults.AuthenticationScheme);

    builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
    {
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.TokenValidationParameters.ValidateLifetime = true;
        o.SaveToken = false; // never keep the raw token around
    });

    auth.AddMcp(o =>
    {
        o.ResourceMetadata = BuildResourceMetadata(entra, entra.PublicBaseUrl);
        o.Events.OnResourceMetadataRequest = ctx =>
        {
            // Derive the resource URL from the request when no public base URL is configured.
            if (string.IsNullOrWhiteSpace(entra.PublicBaseUrl))
            {
                ctx.ResourceMetadata = BuildResourceMetadata(entra, $"{ctx.Request.Scheme}://{ctx.Request.Host}");
            }

            return Task.CompletedTask;
        };
    });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("McpCaller", p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
                ClaimsActingUserResolver.HasScope(ctx.User, entra.RequiredScope) ||
                ClaimsActingUserResolver.HasAppRole(ctx.User, entra.RequiredAppRole)));

    return entra;
}

// ---------------------------------------------------------------------------------------------------------------

static void ConfigureServerOptions(ModelContextProtocol.Server.McpServerOptions options)
{
    string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                     ?? "1.0.0";

    options.ServerInfo = new Implementation
    {
        Name = "teamswork-ticketing",
        Title = "TeamsWork Ticketing",
        Version = version.Split('+')[0],
    };

    options.ServerInstructions =
        "Tools for a TeamsWork Ticketing (Ticketing as a Service) help-desk. " +
        "Tickets are identified by a UUID 'id' (use it for ticketId parameters) and also have a human-readable 'ticketNo'. " +
        "Start with list_tickets or get_ticket for reads. Before creating or updating tickets with custom fields, assignees, or " +
        "custom workflow states, call get_instance to discover the IDs; call list_tag_categories for tag IDs. " +
        "Dates in filters are local to the timezone offset (default US Central). 'expectedDate' must be YYYY-MM-DD. " +
        "All writes are attributed to the authenticated caller; tools never accept a user to impersonate. " +
        "The upstream API allows 100 requests per minute, so prefer 'select' and sensible page sizes.";
}

static string? UserSecretsFilePath()
{
    string? id = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()?.UserSecretsId;
    try
    {
        return id is null ? null : Microsoft.Extensions.Configuration.UserSecrets.PathHelper.GetSecretsPathFromSecretsId(id);
    }
    catch (InvalidOperationException)
    {
        return null; // no usable profile directory (for example a service account without a home folder)
    }
}

static void AddKeyVaultIfConfigured(IConfigurationManager configuration)
{
    // Used for local runs. In Azure the API key is injected as an environment variable from a
    // Key Vault-backed Container Apps secret, so this block is a no-op there.
    string? vaultUri = configuration["KeyVault:Uri"];
    if (string.IsNullOrWhiteSpace(vaultUri))
    {
        return;
    }

    if (!Uri.TryCreate(vaultUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
    {
        throw new StartupConfigurationException("KeyVault:Uri must be an absolute https URL, such as https://<vault-name>.vault.azure.net/.");
    }

    configuration.AddAzureKeyVault(uri, new DefaultAzureCredential());
}

static void AddTicketingServices(IServiceCollection services, IConfiguration configuration, bool requireServiceAccount)
{
    IConfigurationSection section = configuration.GetSection(TicketingOptions.SectionName);
    services.AddOptions<TicketingOptions>()
        // Bound by hand so a value of the wrong type (Ticketing__MaxPageSize=abc) is reported as a setting to fix.
        .Configure(o => StartupConfigurationException.ReadSetting(() => { section.Bind(o); return o; }, TicketingOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey),
            "Ticketing:ApiKey is not set. Use your Ticketing instance's API key (Ticketing app > Settings > API). " +
            "In Azure it is read from Key Vault (KeyVault:Uri, secret Ticketing--ApiKey).")
        .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps,
            "Ticketing:BaseUrl must be an absolute https URL.")
        .Validate(o => o.DefaultPageSize <= o.MaxPageSize, "Ticketing:DefaultPageSize cannot exceed Ticketing:MaxPageSize.")
        // Checked at startup rather than on the first tool call, so a bad zone, or a Linux machine without tzdata,
        // fails where the startup error report and the installer's startup check can show it.
        .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.DefaultTimeZoneId, out _),
            "Ticketing:DefaultTimeZoneId is not a time zone this machine knows. Use an IANA name such as America/Chicago " +
            "(on Linux, the tzdata package provides them).")
        .Validate(o => !requireServiceAccount || o.ServiceAccount?.IsConfigured == true,
            "Ticketing:ServiceAccount:Id, :Name and :Email must all be set when running with --stdio or --local. " +
            "Ticket changes are attributed to this account (Id is your Entra object ID).")
        .ValidateOnStart();

    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<TimeZoneOffsetResolver>();
    services.AddSingleton<TicketingRateLimiter>();

    services.AddHttpClient<TicketingClient>((sp, http) =>
        {
            TicketingOptions o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TicketingOptions>>().Value;
            http.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("teamswork-taas-mcp", "1.0"));
        })
        // The default HttpClient logger writes the full request URI (which carries the API key). Remove it.
        .RemoveAllLoggers();
}

static ProtectedResourceMetadata BuildResourceMetadata(EntraOptions entra, string? publicBaseUrl)
{
    string baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? "http://localhost" : publicBaseUrl.TrimEnd('/');
    return new ProtectedResourceMetadata
    {
        Resource = $"{baseUrl}/mcp",
        AuthorizationServers = { entra.Authority },
        ScopesSupported = { entra.FullScope },
        ResourceName = "TeamsWork Ticketing MCP",
        ResourceDocumentation = "https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow",
    };
}

/// <summary>Exposed so integration tests can host the HTTP transport with WebApplicationFactory.</summary>
public partial class Program
{
    /// <summary>
    /// Set once the resolved run mode has added the user-secrets file as a configuration source, so a startup error
    /// report points there only when the file would actually be read.
    /// </summary>
    internal static bool UserSecretsLoaded { get; private set; }
}
