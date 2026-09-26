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

/// <summary>A person reference supplied by the agent for requestor / assignee fields.</summary>
public sealed record UserRef(
    [property: Description("Microsoft Entra object ID of the person (from get_instance assignees, or a previous ticket). For email-to-ticket requestors, use the email address in all three fields.")]
    [property: JsonPropertyName("id")] string Id,
    [property: Description("Display name.")]
    [property: JsonPropertyName("name")] string Name,
    [property: Description("Email address.")]
    [property: JsonPropertyName("email")] string Email)
{
    internal TicketUser ToTicketUser(string paramName) =>
        new(ToolValidation.RequireText(Id, $"{paramName}.id", 320),
            ToolValidation.RequireText(Name, $"{paramName}.name", 256),
            ToolValidation.RequireEmail(Email, $"{paramName}.email"));
}

/// <summary>A tag reference supplied by the agent.</summary>
public sealed record TagRef(
    [property: Description("ID of the tag category (from list_tag_categories).")]
    [property: JsonPropertyName("tagCategoryId")] string TagCategoryId,
    [property: Description("Tag text exactly as defined in the category.")]
    [property: JsonPropertyName("text")] string Text)
{
    internal TicketTag ToTicketTag(int index) =>
        new(ToolValidation.RequireText(TagCategoryId, $"tags[{index}].tagCategoryId", 64),
            ToolValidation.RequireText(Text, $"tags[{index}].text", 256));
}

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
