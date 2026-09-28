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

    /// <summary>
    /// Validates an opaque upstream ID that goes into a URL path segment. Escaping doesn't stop "." or "..", which the
    /// URI parser then resolves as path navigation, so only the characters these IDs use are allowed.
    /// </summary>
    public static string RequirePathId(string? value, string paramName)
    {
        string id = RequireText(value, paramName, 128);
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id
            : throw new McpException($"'{paramName}' must be an ID made of letters, digits, '-' and '_', as returned by the API.");
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

    /// <summary>
    /// <see cref="RequireText"/> for a one-line value such as a person's name: control characters (C0 and C1, tabs and
    /// line breaks included) are refused. They render as nothing, so "Jane Doe" followed by U+0001 would display as a
    /// listed person's name while comparing as another.
    /// </summary>
    public static string RequireLine(string? value, string paramName, int maxLength)
    {
        string text = RequireText(value, paramName, maxLength);
        if (text.Any(char.IsControl))
        {
            throw new McpException($"'{paramName}' must be one line of text, without control characters.");
        }

        return text;
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
    public static string? OptionalDateOnly(string? value, string paramName) =>
        OptionalDate(value, $"'{paramName}' must be a date in YYYY-MM-DD form (no time component), for example 2026-04-01.")
            ?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Validates a date filter (createdAfter, lastUpdateBefore, ...): a whole day in YYYY-MM-DD form. The API filters
    /// ticket lists by whole days only, and silently ignores a filter with a time of day, returning every ticket as if
    /// none had been given, so one is refused here rather than passed on.
    /// </summary>
    public static DateOnly? OptionalDateFilter(string? value, string paramName) =>
        OptionalDate(value,
            $"'{paramName}' must be a date in YYYY-MM-DD form, for example 2026-04-01. The Ticketing API filters by whole days and " +
            "ignores a time of day; filter by the day, then compare the returned timestamps, which are UTC, for anything finer.");

    /// <summary>The earliest and latest years a date may have: sending one needs a day's room either side.</summary>
    public const int MinYear = 1900, MaxYear = 9998;

    /// <summary>
    /// A YYYY-MM-DD date between <see cref="MinYear"/> and <see cref="MaxYear"/>, or null when blank; anything else is
    /// refused with <paramref name="message"/>. The one parse behind every date the server accepts.
    /// </summary>
    internal static DateOnly? OptionalDate(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) &&
               d.Year is >= MinYear and <= MaxYear
            ? d
            : throw new McpException(message + $" Years {MinYear} to {MaxYear}.");
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

    /// <summary>
    /// An absolute http(s) URL that reads as what it opens: no user name or password before the host (which makes
    /// https://sharepoint.com@evil.example open evil.example), and an ASCII host, with an internationalised name in its
    /// xn-- form, as for email domains, so look-alike letters can't pass for a known site. Send it as
    /// <see cref="Uri.AbsoluteUri"/>, which keeps escapes escaped: <see cref="Uri.ToString"/> would turn %22%3E into
    /// markup and %E2%80%AE into a right-to-left override.
    /// </summary>
    public static Uri RequireHttpUrl(string? value, string paramName)
    {
        value = value?.Trim();
        if (value is { Length: > MaxUrlLength })
        {
            throw new McpException($"'{paramName}' must be at most {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new McpException($"'{paramName}' must be an absolute http(s) URL.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new McpException($"'{paramName}' must not include a user name or password before the host.");
        }

        if (!System.Text.Ascii.IsValid(uri.Host))
        {
            throw new McpException($"'{paramName}' must have an ASCII host name; write an internationalised domain in its xn-- form.");
        }

        return uri;
    }

    public const int MaxUrlLength = 2048;

    /// <summary>
    /// Validates a single, bare email address and returns it in canonical form: the local part as given, the domain in
    /// lower case. MailAddress also accepts lists ("a@x.com, b@y.com") and display names ("Jane &lt;j@y.com&gt;"), where
    /// the address it reports isn't the text given, so anything but one plain address is refused. So is a quoted local
    /// part ("jane@evil.com"@contoso.com), which reads as another address and can carry commas and angle brackets: the
    /// local part must be a dot-atom, as every real mailbox is. The domain must be in
    /// ASCII (an internationalised domain in its punycode form): folding Unicode domains depends on globalisation data the
    /// Linux build doesn't carry, and a full-width "ｃontoso.com" must not pass as, or differ from, contoso.com.
    /// </summary>
    public static string RequireEmail(string? value, string paramName)
    {
        string v = RequireText(value, paramName, 320);
        return CanonicalEmail(v) switch
        {
            { Error: EmailError.None, Canonical: string canonical } => canonical,
            { Error: EmailError.NotAsciiDomain } => throw new McpException(
                $"'{paramName}' must have its domain in ASCII form (for an internationalised domain, its xn-- punycode form)."),
            { Error: EmailError.NotHostName } => throw new McpException(
                $"'{paramName}' must have a domain name, such as example.com (not an IP address)."),
            _ => throw new McpException($"'{paramName}' must be one plain email address, such as name@example.com."),
        };
    }

    public enum EmailError { None, NotOneAddress, NotAsciiDomain, NotHostName }

    /// <summary>
    /// The checks behind <see cref="RequireEmail"/>, for callers that report problems their own way (configuration). The
    /// canonical form is the one every address is compared and sent in.
    /// </summary>
    public static (string? Canonical, EmailError Error) CanonicalEmail(string value)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(value, out System.Net.Mail.MailAddress? address) ||
            !string.IsNullOrEmpty(address.DisplayName) ||
            !string.Equals(address.Address, value, StringComparison.Ordinal) ||
            !IsDotAtom(address.User))
        {
            return (null, EmailError.NotOneAddress);
        }

        if (!address.Host.All(char.IsAscii))
        {
            return (null, EmailError.NotAsciiDomain);
        }

        return IsHostName(address.Host) ? (address.User + "@" + address.Host.ToLowerInvariant(), EmailError.None) : (null, EmailError.NotHostName);
    }

    /// <summary>
    /// Whether an address's local part is a dot-atom: no quotes, backslashes, brackets, parentheses, commas, colons,
    /// semicolons, at signs, spaces, or control characters, and dots only between other characters. Letters outside
    /// ASCII are allowed, as internationalised mailboxes use them.
    /// </summary>
    private static bool IsDotAtom(string local) =>
        local.Length > 0 && local[0] != '.' && local[^1] != '.' && !local.Contains("..", StringComparison.Ordinal) &&
        !local.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "\"\\()<>[],:;@".Contains(c, StringComparison.Ordinal));

    /// <summary>The domain of an address from <see cref="RequireEmail"/>, lower case.</summary>
    public static string EmailDomain(string address) => address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="domain"/> is a DNS host name in ASCII: two or more labels of letters, digits and hyphens,
    /// each 1 to 63 characters and not starting or ending with a hyphen, 253 characters at most, and a last label that
    /// isn't all digits (so an IP address isn't one). Punycode labels (xn--) pass; IP literals in brackets don't, since
    /// no help desk user has one and they can't be put in an allowlist.
    /// </summary>
    public static bool IsHostName(string domain)
    {
        if (domain.Length is 0 or > 253)
        {
            return false;
        }

        string[] labels = domain.Split('.');
        return labels.Length >= 2 &&
               labels.All(l => l.Length is >= 1 and <= 63 && l[0] != '-' && l[^1] != '-' && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) &&
               !labels[^1].All(char.IsAsciiDigit);
    }
}
