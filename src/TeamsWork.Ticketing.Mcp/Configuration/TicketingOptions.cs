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

    /// <summary>The US (global) endpoint, used unless <see cref="Region"/> or <see cref="BaseUrl"/> says otherwise.</summary>
    public const string DefaultBaseUrl = "https://teamswork.azure-api.net/ticketing/v1";

    /// <summary>The vendor's regional endpoints, keyed by the <see cref="Region"/> names this server accepts.</summary>
    public static readonly IReadOnlyDictionary<string, string> RegionBaseUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = DefaultBaseUrl,
        ["EU"] = "https://ticketing-apim-eu.azure-api.net/ticketing/v1",
        ["AUS"] = "https://ticketing-apim-aus.azure-api.net/ticketing/v1",
    };

    /// <summary>Base URL of the Ticketing REST API (no trailing slash required).</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Data region of the Ticketing instance: US (the default), EU, or AUS. Picks the matching vendor endpoint, so
    /// <see cref="BaseUrl"/> only needs setting for an endpoint the vendor adds later.
    /// </summary>
    public string? Region { get; set; }

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

    /// <summary>
    /// Largest upstream response the server will read, in bytes. A full page of tickets with HTML descriptions is well
    /// under 1 MB; the limit stops a runaway response from exhausting the container's memory.
    /// </summary>
    [Range(64 * 1024, 256 * 1024 * 1024)]
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// How long instance settings and tag categories are cached, in seconds. They change rarely, and every lookup of a
    /// person, tag, or custom field by name reads them. 0 turns the cache off.
    /// </summary>
    [Range(0, 86_400)]
    public int InstanceCacheSeconds { get; set; } = 300;

    /// <summary>
    /// Most tickets one call to a filtering tool (list_my_tickets, list_sla_risk, count_tickets) reads. The API has no
    /// filter for assignee, requestor, or SLA state, so those tools page through tickets and filter them here.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxScanTickets { get; set; } = 1000;

    /// <summary>
    /// Upstream requests one caller may cause per minute (Entra mode), counting every request a tool call makes, so
    /// one caller can't use up the quota all callers share. Keep it below <see cref="RateLimitPermits"/>. 0 turns it off.
    /// </summary>
    [Range(0, 10_000)]
    public int MaxUpstreamRequestsPerCallerPerMinute { get; set; } = 50;

    /// <summary>
    /// Folder that upload_ticket_files may read from (stdio only). The tool is offered only when this is set, and it
    /// refuses any file outside it, so an agent steered by text it has read can't send arbitrary local files.
    /// </summary>
    public string? UploadRoot { get; set; }

    /// <summary>Largest total size of the files in one upload_ticket_files call, in bytes.</summary>
    [Range(1, 100 * 1024 * 1024)]
    public int MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Comma-separated email domains (for example "contoso.com, contoso.co.uk") that people outside the instance's
    /// assignee list may have when named as a requestor or in a people field. Unset allows any domain. Set it so text an
    /// agent has read can't make an outside address the requestor of a ticket, and so receive its notifications.
    /// </summary>
    public string? ExternalEmailDomains { get; set; }

    /// <summary>
    /// The domains in <see cref="ExternalEmailDomains"/>, lower case. Matching is exact (a subdomain is a different
    /// domain), and every entry must be an ASCII host name (punycode for an internationalised one); startup refuses
    /// anything else, so a typo or wildcard can't silently match nothing.
    /// </summary>
    public IReadOnlySet<string> ExternalEmailDomainSet() =>
        ExternalEmailDomainEntries().Select(d => d.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Entries of <see cref="ExternalEmailDomains"/> that aren't plain ASCII host names.</summary>
    public IReadOnlyList<string> InvalidExternalEmailDomains() =>
        ExternalEmailDomainEntries()
            .Where(d => d.StartsWith('.') || d.EndsWith('.') || !d.Contains('.') ||
                        !d.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            .ToList();

    private IEnumerable<string> ExternalEmailDomainEntries() =>
        (ExternalEmailDomains ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@'))
            .Where(d => d.Length > 0);

    /// <summary>The endpoint <see cref="Region"/> names, or null when it is unset or not a known region.</summary>
    public string? RegionBaseUrl() =>
        string.IsNullOrWhiteSpace(Region) ? null : RegionBaseUrls.GetValueOrDefault(Region.Trim());
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

    /// <summary>
    /// Whether the identity is one the help desk can know: a valid email, and an ID that is either an Entra object ID
    /// (a GUID other than all zeros) or that same email (the email-to-ticket form). Anything else attributes every write
    /// to someone the help desk doesn't know, without any error.
    /// </summary>
    public bool IsValidIdentity =>
        IsConfigured &&
        System.Net.Mail.MailAddress.TryCreate(Email!.Trim(), out System.Net.Mail.MailAddress? address) &&
        string.Equals(address.Address, Email.Trim(), StringComparison.OrdinalIgnoreCase) &&
        TeamsWork.Ticketing.Mcp.Tools.ToolValidation.IsAsciiDomain(address.Host) &&
        // The standard 36-character form only: the help desk stores that form and people are matched on it.
        ((Guid.TryParseExact(Id!.Trim(), "D", out Guid objectId) && objectId != Guid.Empty) ||
         string.Equals(Id!.Trim(), Email.Trim(), StringComparison.OrdinalIgnoreCase));
}
