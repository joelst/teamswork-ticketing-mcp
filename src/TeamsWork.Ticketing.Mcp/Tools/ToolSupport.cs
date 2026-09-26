using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>Serialises tool results compactly (nulls omitted) so responses stay small for the model.</summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Runs a tool body and converts the failures whose messages are written for the agent into
/// <see cref="McpException"/>s, the only exceptions whose message the SDK forwards to the client. Anything else is
/// unexpected: it propagates, and the SDK logs it and returns a generic error, so internal details (type names,
/// configuration, library messages) never reach the client.
/// </summary>
internal static class ToolRunner
{
    public static async Task<string> RunAsync<T>(Func<Task<T>> body)
    {
        try
        {
            T result = await body();
            return ToolJson.Serialize(result);
        }
        catch (McpException)
        {
            throw;
        }
        catch (TicketingApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (ActingUserException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}

/// <summary>Page envelope returned by list tools.</summary>
internal sealed record PageResult<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("totalCount")] int? TotalCount,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken,
    [property: JsonPropertyName("hint")] string? Hint);

/// <summary>
/// Envelope for tools that page through tickets and filter them on the server, because the API can't filter on what
/// they need. <c>truncated</c> is true when the scan stopped at Ticketing:MaxScanTickets before reading every ticket.
/// </summary>
internal sealed record ScanResult<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("matched")] int Matched,
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("totalCount")] int? TotalCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("hint")] string? Hint);

/// <summary>The fields of a ticket that triage needs, so tools that return many tickets stay small.</summary>
internal sealed record TicketSummary(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("ticketNo")] int? TicketNo,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("priority")] string? Priority,
    [property: JsonPropertyName("assignee")] TicketUser? Assignee,
    [property: JsonPropertyName("requestor")] TicketUser? Requestor,
    [property: JsonPropertyName("expectedDate")] string? ExpectedDate,
    [property: JsonPropertyName("createdOn")] string? CreatedOn,
    [property: JsonPropertyName("lastUpdatedOn")] string? LastUpdatedOn,
    [property: JsonPropertyName("isFrtBreached")] bool? IsFrtBreached,
    [property: JsonPropertyName("isRtBreached")] bool? IsRtBreached,
    [property: JsonPropertyName("isFrtEscalated")] bool? IsFrtEscalated,
    [property: JsonPropertyName("isRtEscalated")] bool? IsRtEscalated)
{
    public static TicketSummary From(Ticket t) => new(
        t.Id, TicketNumber(t), t.Title, t.Status, t.Priority, t.Assignee, t.Requestor, t.ExpectedDate, t.CreatedOn,
        t.LastUpdatedOn, t.IsFrtBreached, t.IsRtBreached, t.IsFrtEscalated, t.IsRtEscalated);

    /// <summary>
    /// The human ticket number. The API returns it as ticketNo, but a 'select' names the field ticketId, so a selected
    /// response may carry it under that name instead.
    /// </summary>
    public static int? TicketNumber(Ticket t)
    {
        if (t.TicketNo is int n)
        {
            return n;
        }

        if (t.Extra?.TryGetValue("ticketId", out JsonElement v) == true)
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int number))
            {
                return number;
            }

            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }
}

/// <summary>Envelope for write tools: what changed and who it was attributed to.</summary>
internal sealed record WriteResult<T>(
    [property: JsonPropertyName("item")] T Item,
    [property: JsonPropertyName("actedAs")] ActedAs ActedAs);

internal sealed record ActedAs(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("source")] string Source)
{
    public static ActedAs From(ActingUser user) => new(user.Name, user.Email, user.Source.ToString());
}

/// <summary>
/// A person reference supplied by the agent for requestor / assignee fields. All three fields together are used as
/// given; an email or name alone is looked up in the instance's assignee list.
/// </summary>
public sealed record UserRef(
    [property: Description("Microsoft Entra object ID of the person. Only needed, with name and email, for someone outside the get_instance assignee list. For email-to-ticket requestors, use the email address in all three fields.")]
    [property: JsonPropertyName("id")] string? Id = null,
    [property: Description("Display name. On its own, it is looked up in the assignee list (a unique partial match is enough).")]
    [property: JsonPropertyName("name")] string? Name = null,
    [property: Description("Email address. On its own, it is looked up in the assignee list.")]
    [property: JsonPropertyName("email")] string? Email = null)
{
    internal bool IsComplete =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);

    internal TicketUser ToTicketUser(string paramName) =>
        new(ToolValidation.RequireText(Id, $"{paramName}.id", 320),
            ToolValidation.RequireText(Name, $"{paramName}.name", 256),
            ToolValidation.RequireEmail(Email, $"{paramName}.email"));
}

/// <summary>A tag reference supplied by the agent. Resolved against the instance's tag categories.</summary>
public sealed record TagRef(
    [property: Description("ID or name of the tag category (from list_tag_categories).")]
    [property: JsonPropertyName("tagCategoryId")] string TagCategoryId,
    [property: Description("Tag text as defined in the category (case doesn't matter).")]
    [property: JsonPropertyName("text")] string Text);

/// <summary>A hyperlink attachment supplied by the agent.</summary>
public sealed record LinkRef(
    [property: Description("Absolute http(s) URL to attach.")]
    [property: JsonPropertyName("url")] string Url,
    [property: Description("Caption shown for the link.")]
    [property: JsonPropertyName("caption")] string Caption)
{
    internal AttachmentLink ToAttachmentLink(int index) =>
        new(ToolValidation.RequireHttpUrl(Url, $"links[{index}].url").ToString(),
            ToolValidation.RequireText(Caption, $"links[{index}].caption", 256));
}
