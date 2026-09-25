using System.ComponentModel.DataAnnotations;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// Settings for the upstream TeamsWork Ticketing API. Bound from the <c>Ticketing</c> configuration section.
/// The API key must come from an environment variable, user secrets, or Azure Key Vault. It is never stored in
/// appsettings files.
/// </summary>
public sealed class TicketingOptions
{
    public const string SectionName = "Ticketing";

    /// <summary>Base URL of the Ticketing REST API (no trailing slash required).</summary>
    [Required]
    public string BaseUrl { get; set; } = "https://teamswork.azure-api.net/ticketing/v1";

    /// <summary>Ticketing instance API key. Sent as the <c>key</c> query parameter on every upstream call.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// IANA time zone used to compute the <c>timezone</c> offset the API requires when a tool call does not
    /// supply one explicitly. Defaults to US Central; set it to the help-desk's local zone.
    /// </summary>
    [Required]
    public string DefaultTimeZoneId { get; set; } = "America/Chicago";

    /// <summary>
    /// Identity recorded as the actor for write operations when the caller is an application (no user claims),
    /// for example a Foundry project managed identity, or when running over stdio without an Entra token.
    /// </summary>
    public ServiceAccountOptions? ServiceAccount { get; set; }

    /// <summary>Upstream rate limit: permits per window. The vendor enforces 100 requests per 60 seconds.</summary>
    [Range(1, 10_000)]
    public int RateLimitPermits { get; set; } = 100;

    /// <summary>Upstream rate limit window in seconds.</summary>
    [Range(1, 3600)]
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>Largest page size a tool will request from the API (the API allows up to 1000).</summary>
    [Range(1, 1000)]
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Page size used when a tool call does not specify one.</summary>
    [Range(1, 1000)]
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>HTTP timeout for a single upstream request.</summary>
    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>A fixed identity used to attribute writes when no user identity is available.</summary>
public sealed class ServiceAccountOptions
{
    /// <summary>Entra object ID (or email for email-to-ticket style identities).</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Email { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);
}
