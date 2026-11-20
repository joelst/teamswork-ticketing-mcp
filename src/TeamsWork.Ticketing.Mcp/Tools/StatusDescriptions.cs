using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using TeamsWork.Ticketing.Mcp.Ticketing;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Puts the help desk's own workflow states into list_tickets' 'status' description when tools are listed, so an agent
/// knows them (and that one state isn't every open ticket) without calling get_instance first. The states come from the
/// cached instance settings; when they can't be read quickly, the fixed description stands, and a failure is remembered
/// for a while so listing tools doesn't keep waiting on an unreachable API.
/// </summary>
internal static class StatusDescriptions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static long _retryAfterTicks;

    public static IMcpServerBuilder WithInstanceStates(this IMcpServerBuilder mcp) =>
        mcp.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            ListToolsResult result = await next(request, cancellationToken);
            if (request.Services is not IServiceProvider services || await DescribeAsync(services, cancellationToken) is not string text)
            {
                return result;
            }

            for (int i = 0; i < result.Tools.Count; i++)
            {
                if (result.Tools[i].Name == "list_tickets")
                {
                    result.Tools[i] = WithStatusDescription(result.Tools[i], text);
                }
            }

            return result;
        }));

    /// <summary>The 'status' description naming the instance's states, or null to keep the fixed one.</summary>
    internal static string? Describe(JsonElement? workflows)
    {
        List<string> states = WorkflowStatus.StatesIn(workflows).Count > 0 ? WorkflowStatus.StateNames(workflows) : [];
        return states.Count == 0
            ? null
            : $"Only tickets in exactly this state: {string.Join(", ", states)}. For every open or every closed ticket, use isResolved instead.";
    }

    private static async Task<string?> DescribeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _retryAfterTicks))
        {
            return null;
        }

        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(Wait);
            InstanceCache cache = services.GetRequiredService<InstanceCache>();
            TicketingClient client = services.GetRequiredService<TicketingClient>();
            return Describe((await cache.GetInstanceAsync(client, null, refresh: false, wait.Token)).Workflows);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the wait ended: the shared read carries on and fills the cache, so the next listing has the states.
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref _retryAfterTicks, DateTime.UtcNow.Add(RetryAfterFailure).Ticks);
            return null;
        }
    }

    private static Tool WithStatusDescription(Tool tool, string text)
    {
        JsonNode? schema = JsonNode.Parse(tool.InputSchema.GetRawText());
        if (schema?["properties"]?["status"] is not JsonObject status)
        {
            return tool;
        }

        status["description"] = text;
        return new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = JsonSerializer.SerializeToElement(schema),
            OutputSchema = tool.OutputSchema,
            Annotations = tool.Annotations,
            Icons = tool.Icons,
            Meta = tool.Meta,
        };
    }
}
