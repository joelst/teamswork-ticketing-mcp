using System.ComponentModel;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

[McpServerToolType]
public sealed class InstanceTools
{
    private readonly TicketingClient _client;

    public InstanceTools(TicketingClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "get_instance", Title = "Get ticketing instance settings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get the Ticketing instance configuration: custom field definitions (IDs, types, options, whether mandatory), the assignee list, " +
        "workflow definitions with state IDs, and SLA settings. Call this before create_ticket or update_ticket when you need to fill " +
        "custom fields, pick an assignee, or use a custom workflow state.")]
    public Task<string> GetInstance(
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(() => _client.GetInstanceAsync(timezoneOffset, cancellationToken));
    }

    [McpServerTool(Name = "list_tag_categories", Title = "List tag categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List all tag categories and the tags in each. Use the category ID with a tag's text for create_ticket/update_ticket tags and for the list_tickets 'tags' filter (tagCategoryId_tagText).")]
    public Task<string> ListTagCategories(CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            ListResponse<TagCategory> r = await _client.ListTagCategoriesAsync(cancellationToken);
            List<TagCategory> items = (r.Items ?? []).Where(c => c.Deleted != true).ToList();
            return new PageResult<TagCategory>(items, items.Count, null, null, null);
        });
    }
}
