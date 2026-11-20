using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Tickets are given by number, and a model or client may send 2213 as a JSON number rather than the string "2213". The
/// SDK would fail to bind that with a message naming neither the parameter nor the fix, so before a tool call is bound,
/// a number in 'ticket', or among 'tickets', becomes the same digits as a string.
/// </summary>
internal static class TicketArguments
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            if (request.Params?.Arguments is { } arguments)
            {
                if (arguments.TryGetValue("ticket", out JsonElement one) && one.ValueKind == JsonValueKind.Number)
                {
                    arguments["ticket"] = AsString(one);
                }

                if (arguments.TryGetValue("tickets", out JsonElement many) && many.ValueKind == JsonValueKind.Array &&
                    many.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Number))
                {
                    arguments["tickets"] = JsonSerializer.SerializeToElement(
                        many.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? (object)e.GetRawText() : e).ToList());
                }
            }

            return next(request, cancellationToken);
        };

    private static JsonElement AsString(JsonElement number) => JsonSerializer.SerializeToElement(number.GetRawText());
}
