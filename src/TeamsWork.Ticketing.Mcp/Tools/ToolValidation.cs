using System.Globalization;
using ModelContextProtocol;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Argument validation shared by all tools. Tool arguments are untrusted JSON from the client; every value that
/// ends up in an upstream URL or body is checked here first. Failures throw <see cref="McpException"/> so the
/// message reaches the agent verbatim.
/// </summary>
internal static class ToolValidation
{
    public static readonly string[] Priorities = ["Low", "Medium", "Important", "Urgent"];
    public static readonly string[] LegacyStatuses = ["Open", "Reopened", "In Progress", "Resolved", "Closed"];
    public static readonly string[] Resolutions = ["fixed", "cannotResolve", "cancelled"];
    public static readonly string[] OrderByFields =
    [
        "status", "ticketId", "title", "requestorName", "requestorEmail", "assigneeName", "assigneeEmail",
        "expectedDate", "priority", "createdDateTime", "lastInteraction",
    ];
    public static readonly string[] SelectableFields =
    [
        "id", "ticketId", "title", "description", "status", "requestor", "customFields", "priority", "assignee",
        "expectedDate", "resolution", "firstResponseOn", "firstResolutionOn", "lastResolutionOn", "createdOn", "tags",
        "lastUpdatedOn", "isFrtEscalated", "isRtEscalated", "createdBy", "lastUpdatedBy", "lastResolutionComment",
    ];

    public static Guid RequireGuid(string? value, string paramName)
    {
        if (!Guid.TryParse(value, out Guid guid) || guid == Guid.Empty)
        {
            throw new McpException($"'{paramName}' must be a ticket UUID (for example 3fa85f64-5717-4562-b3fc-2c963f66afa6). Use list_tickets to find it.");
        }

        return guid;
    }

    public static string RequireText(string? value, string paramName, int maxLength = 20_000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException($"'{paramName}' is required.");
        }

        if (value.Length > maxLength)
        {
            throw new McpException($"'{paramName}' is too long (max {maxLength} characters).");
        }

        return value.Trim();
    }

    public static string? OptionalText(string? value, string paramName, int maxLength = 20_000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new McpException($"'{paramName}' is too long (max {maxLength} characters).");
        }

        return value.Trim();
    }

    /// <summary>Validates a value against an allowed set, case-insensitively, and returns the canonical spelling.</summary>
    public static string? OptionalEnum(string? value, string paramName, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string? match = allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new McpException($"'{paramName}' must be one of: {string.Join(", ", allowed)}.");
    }

    public static string RequireEnum(string? value, string paramName, IReadOnlyList<string> allowed) =>
        OptionalEnum(value, paramName, allowed) ?? throw new McpException($"'{paramName}' is required and must be one of: {string.Join(", ", allowed)}.");

    /// <summary>Validates a date-only value in YYYY-MM-DD form. The API silently fails on datetime values here.</summary>
    public static string? OptionalDateOnly(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            throw new McpException($"'{paramName}' must be a date in YYYY-MM-DD form (no time component), for example 2026-04-01.");
        }

        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Validates a datetime filter in the API's YYYY-MM-DDTHH:mm:ss form (a date-only value is expanded to midnight).</summary>
    public static string? OptionalDateTime(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string v = value.Trim();
        if (DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00";
        }

        if (DateTime.TryParseExact(v, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        throw new McpException($"'{paramName}' must be YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss (local time for the timezone offset), for example 2026-04-01T09:30:00.");
    }

    public static int ResolvePageSize(int? requested, int defaultSize, int maxSize)
    {
        if (requested is null)
        {
            return defaultSize;
        }

        if (requested < 1)
        {
            throw new McpException("'limit' must be at least 1.");
        }

        return Math.Min(requested.Value, maxSize);
    }

    public static int OptionalOffset(int? offset)
    {
        if (offset is < 0)
        {
            throw new McpException("'offset' cannot be negative.");
        }

        return offset ?? 0;
    }

    /// <summary>Validates a comma-separated select list against the fields the API documents.</summary>
    public static string? OptionalSelect(string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
        {
            return null;
        }

        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var unknown = fields.Where(f => !SelectableFields.Contains(f, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new McpException($"'select' contains unknown fields: {string.Join(", ", unknown)}. Allowed: {string.Join(", ", SelectableFields)}.");
        }

        return string.Join(',', fields);
    }

    /// <summary>
    /// Validates an opaque paging token. It is sent upstream as an HTTP header value, and the .NET HTTP stack writes
    /// a value added without validation to the wire as is, so a CR/LF would start a new header (or request). Only
    /// visible ASCII is allowed, which covers the base64 and URL-safe forms these tokens take.
    /// </summary>
    public static string? OptionalToken(string? value, string paramName, int maxLength = 4000)
    {
        string? token = OptionalText(value, paramName, maxLength);
        if (token is not null && !Ticketing.TicketingClient.IsSafeHeaderValue(token))
        {
            throw new McpException($"'{paramName}' is not a token this server issued. Pass back the continuationToken value unchanged.");
        }

        return token;
    }

    /// <summary>
    /// Sanitises HTML the agent wants stored in a ticket or comment. The help desk renders it for staff, and an agent
    /// can be steered by text it has read (prompt injection), so script, event handlers, frames, and javascript: URLs
    /// are removed here rather than trusting the vendor to do it.
    /// </summary>
    public static string? OptionalHtml(string? value, string paramName, int maxLength = 20_000)
    {
        string? html = OptionalText(value, paramName, maxLength);
        if (html is null)
        {
            return null;
        }

        string clean = HtmlSanitizerInstance.Value.Sanitize(html).Trim();
        return clean.Length > 0
            ? clean
            : throw new McpException($"'{paramName}' has no content left after removing unsafe HTML (scripts, event handlers, frames).");
    }

    // Starts from HtmlSanitizer's defaults (no script, event handlers, or frames; http, https and relative URLs only)
    // and narrows them to what a help-desk comment needs: text formatting, headings, lists, tables, quotes, code, and
    // links. The defaults also allow things that are dangerous in HTML staff will view:
    //  - form controls, which make a working credential-phishing form;
    //  - style, which can draw a full-screen overlay (position: fixed) or load a url() beacon;
    //  - images and image maps, which load as soon as the ticket is viewed, so an agent steered by injected text
    //    could put data it has read in the image URL (screenshots belong in add_ticket_link_attachments);
    //  - name, which lets markup replace named properties on the page's document (DOM clobbering);
    //  - target, which without rel=noopener lets the opened page navigate the help desk tab (reverse tabnabbing).
    // Sanitize is thread-safe on a shared instance as long as its settings aren't changed after this.
    private static readonly Lazy<Ganss.Xss.HtmlSanitizer> HtmlSanitizerInstance = new(() =>
    {
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        foreach (string tag in (string[])
                 ["form", "input", "button", "select", "option", "optgroup", "textarea", "keygen", "datalist", "output",
                  "fieldset", "legend", "label", "menu", "menuitem", "img", "area", "map", "html", "head", "body"])
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        foreach (string attribute in (string[])
                 ["style", "name", "target", "src", "longdesc", "usemap", "ismap", "action", "method", "enctype",
                  "accept", "accept-charset", "autocomplete", "novalidate", "contenteditable", "draggable", "dropzone",
                  "tabindex", "accesskey"])
        {
            sanitizer.AllowedAttributes.Remove(attribute);
        }

        return sanitizer;
    });

    /// <summary>Rejects lists longer than <paramref name="max"/>, so one call can't build an arbitrarily large request.</summary>
    public static void MaxCount<T>(IReadOnlyCollection<T>? items, string paramName, int max)
    {
        if (items is not null && items.Count > max)
        {
            throw new McpException($"'{paramName}' can have at most {max} entries.");
        }
    }

    public static Uri RequireHttpUrl(string? value, string paramName)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new McpException($"'{paramName}' must be an absolute http(s) URL.");
        }

        return uri;
    }

    public static string RequireEmail(string? value, string paramName)
    {
        string v = RequireText(value, paramName, 320);
        if (!System.Net.Mail.MailAddress.TryCreate(v, out _))
        {
            throw new McpException($"'{paramName}' must be a valid email address.");
        }

        return v;
    }
}
