namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// Microsoft Entra ID settings. Bound from the <c>Entra</c> configuration section, which is also the section
/// Microsoft.Identity.Web reads (<c>Instance</c>, <c>TenantId</c>, <c>ClientId</c>).
/// </summary>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";

    /// <summary>Authority host. Single-tenant deployments keep the default.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Directory (tenant) ID that issues tokens for this server.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) ID of the server app registration. Tokens must be issued for this audience.</summary>
    public string? ClientId { get; set; }

    /// <summary>Delegated scope a user token must carry (as exposed on the server app registration).</summary>
    public string RequiredScope { get; set; } = "access_as_user";

    /// <summary>Application role an app-only token must carry (as defined on the server app registration).</summary>
    public string RequiredAppRole { get; set; } = "Ticketing.ReadWrite";

    /// <summary>
    /// Public base URL of this server (for example <c>https://taas-mcp.example.azurecontainerapps.io</c>). Used to
    /// build the OAuth protected resource metadata. When empty the value is derived from the incoming request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Application ID URI (<c>api://{clientId}</c>) used as the token resource / audience.</summary>
    public string ApplicationIdUri => $"api://{ClientId}";

    /// <summary>Fully qualified delegated scope, for example <c>api://{clientId}/access_as_user</c>.</summary>
    public string FullScope => $"{ApplicationIdUri}/{RequiredScope}";

    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";
}
