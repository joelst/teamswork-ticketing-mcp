using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// A call that leaves out a required argument would fail to bind with an exception the client sees only as a generic
/// error, which a model can take for the server being unreachable. So before a tool call is bound, the arguments are
/// checked against the tool's input schema, and a missing one is answered as a tool error that names it, says what it
/// takes, and lists the names the call did send (often the same value under another name, such as ticketId).
/// </summary>
internal static class RequiredArguments
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            if (request.MatchedPrimitive is McpServerTool tool && Missing(tool.ProtocolTool, request.Params?.Arguments) is { } message)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = message }],
                });
            }

            return next(request, cancellationToken);
        };

    /// <summary>The error for the first required argument <paramref name="arguments"/> lacks, or null when none is.</summary>
    internal static string? Missing(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        JsonElement schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("required", out JsonElement required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement entry in required.EnumerateArray())
        {
            string? name = entry.GetString();
            if (name is null ||
                (arguments is not null && arguments.TryGetValue(name, out JsonElement value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))
            {
                continue;
            }

            string described = schema.TryGetProperty("properties", out JsonElement properties) &&
                               properties.TryGetProperty(name, out JsonElement property) &&
                               property.TryGetProperty("description", out JsonElement description) &&
                               description.GetString() is { Length: > 0 } text
                ? $" ({text.TrimEnd('.')})"
                : "";
            string sent = arguments is { Count: > 0 }
                ? $" The call sent {string.Join(", ", arguments.Keys.Order(StringComparer.Ordinal))}."
                : " The call sent no arguments.";
            return $"{tool.Name} needs '{name}'{described}.{sent} Call it again with '{name}'.";
        }

        return null;
    }
}
