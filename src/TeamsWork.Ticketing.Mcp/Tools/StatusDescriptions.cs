using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using TeamsWork.Ticketing.Mcp.Ticketing;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Puts the help desk's own workflow states into list_tickets' 'status' description when tools are listed, so an agent
/// knows them (and that one state isn't every open ticket) without calling get_instance first. The states come from the
/// cached instance settings; when they can't be read quickly, the fixed description stands, and a failure or a slow read
/// is remembered for a while so listing tools doesn't keep waiting on an unreachable API.
/// </summary>
internal static class StatusDescriptions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>When listing tools may next start a read of the settings, kept for the whole server.</summary>
    internal sealed class BackOff(TimeProvider time)
    {
        private long _untilTicks;

        public bool Active => time.GetUtcNow().UtcTicks < Interlocked.Read(ref _untilTicks);

        public void Start() => Interlocked.Exchange(ref _untilTicks, time.GetUtcNow().Add(RetryAfterFailure).UtcTicks);
    }

    public static IMcpServerBuilder WithInstanceStates(this IMcpServerBuilder mcp)
    {
        mcp.Services.TryAddSingleton(sp => new BackOff(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return mcp.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, cancellationToken) =>
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
    }

    /// <summary>The 'status' description naming the instance's states, or null to keep the fixed one.</summary>
    internal static string? Describe(JsonElement? workflows)
    {
        List<string> states = WorkflowStatus.StatesIn(workflows).Count > 0 ? WorkflowStatus.StateNames(workflows) : [];
        return states.Count == 0
            ? null
            : $"Only tickets in exactly this state: {string.Join(", ", states)}. For every open or every closed ticket, use isResolved instead.";
    }

    internal static async Task<string?> DescribeAsync(IServiceProvider services, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        BackOff? backOff = services.GetService<BackOff>();
        try
        {
            // A copy in the cache is used even while backing off, so a read that finished after its wait still counts.
            InstanceCache cache = services.GetRequiredService<InstanceCache>();
            if (cache.CachedInstance(null) is { } cached)
            {
                return Describe(cached.Workflows);
            }

            if (backOff?.Active == true)
            {
                return null;
            }

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(wait ?? Wait);
            TicketingClient client = services.GetRequiredService<TicketingClient>();
            return Describe((await cache.GetInstanceAsync(client, null, refresh: false, waiting.Token)).Workflows);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the wait ended: the shared read carries on and, if it succeeds, fills the cache for the next listing.
            // Backing off all the same means a read that then fails isn't started again by every listing during an outage.
            backOff?.Start();
            return null;
        }
        catch (TicketingApiException ex) when (ex.IsRateLimited && !cancellationToken.IsCancellationRequested)
        {
            // One caller's used-up share (or a moment's rate limit) isn't the API failing: no back-off, which every caller
            // would share, so the next listing tries again.
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            backOff?.Start();
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
