using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Serialises tool results compactly (nulls omitted) so responses stay small for the model. The text goes to a model,
/// not into a web page, so only what JSON itself requires is escaped: the default encoder writes &lt; &gt; &amp; ' and
/// every non-ASCII character as \uXXXX, which makes each "Name &lt;email&gt;" ten characters longer and harder to read.
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>The same JSON as a node, for adding a computed member to an API object without retyping it.</summary>
    public static System.Text.Json.Nodes.JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
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
        new(ToolValidation.RequireLine(Id, $"{paramName}.id", 320),
            ToolValidation.RequireLine(Name, $"{paramName}.name", 256),
            ToolValidation.RequireEmail(Email, $"{paramName}.email"));
}

/// <summary>
/// A tag reference supplied by the agent. Resolved against the instance's tag categories. Properties rather than a
/// positional record, so the schema can require the text while leaving the category optional.
/// </summary>
public sealed record TagRef
{
    [Description("ID or name of the tag category (from list_tag_categories). Can be left out when the tag's text is in one category only.")]
    [JsonPropertyName("tagCategoryId")]
    public string? TagCategoryId { get; init; }

    [Description("Tag text as defined in the category (case doesn't matter).")]
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>A hyperlink attachment supplied by the agent.</summary>
public sealed record LinkRef(
    [property: Description("Absolute http(s) URL to attach.")]
    [property: JsonPropertyName("url")] string Url,
    [property: Description("Caption shown for the link.")]
    [property: JsonPropertyName("caption")] string Caption)
{
    internal AttachmentLink ToAttachmentLink(int index) =>
        new(ToolValidation.RequireHttpUrl(Url, $"links[{index}].url").AbsoluteUri,
            ToolValidation.RequireText(Caption, $"links[{index}].caption", 256));
}
