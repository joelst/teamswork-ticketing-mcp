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
