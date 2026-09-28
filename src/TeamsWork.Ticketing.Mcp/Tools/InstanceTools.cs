using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

[McpServerToolType]
public sealed class InstanceTools
{
    private static readonly string[] Sections = ["all", "customFields", "assignees", "workflows", "sla"];

    private readonly TicketingClient _client;
    private readonly InstanceCache _cache;

    public InstanceTools(TicketingClient client, InstanceCache cache)
    {
        _client = client;
        _cache = cache;
    }

    [McpServerTool(Name = "get_instance", Title = "Get ticketing instance settings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get the Ticketing instance configuration: custom field definitions (IDs, types, options, whether mandatory), the assignee list, " +
        "workflow definitions with state IDs, and SLA settings. Pass 'section' to get only the part you need; the full response is large. " +
        "Results are cached for a few minutes; pass refresh=true after changing the instance's settings (a refresh re-reads unless the " +
        "settings were read in the last 30 seconds).")]
    public Task<string> GetInstance(
        [Description("Part to return: all (default), customFields (custom and optional fields), assignees, workflows, or sla.")] string? section = null,
        [Description("true to re-read the settings from the API, unless they were read in the last 30 seconds.")] bool refresh = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string part = ToolValidation.OptionalEnum(section, "section", Sections) ?? "all";
            Instance instance = await _cache.GetInstanceAsync(_client, timezoneOffset, refresh, cancellationToken);
            return part switch
            {
                "customFields" => new InstanceSection(instance)
                {
                    CustomFields = instance.CustomFields,
                    CustomFieldsLeft = instance.CustomFieldsLeft,
                    CustomFieldsRight = instance.CustomFieldsRight,
                    OptionalFieldsLeft = instance.OptionalFieldsLeft,
                    OptionalFieldsRight = instance.OptionalFieldsRight,
                },
                "assignees" => new InstanceSection(instance) { Assignees = instance.Assignees, IsAutomaticAssignTickets = instance.IsAutomaticAssignTickets },
                "workflows" => new InstanceSection(instance) { Workflows = instance.Workflows },
                "sla" => new InstanceSection(instance) { Sla = instance.Sla },
                _ => (object)instance,
            };
        });
    }

    [McpServerTool(Name = "list_tag_categories", Title = "List tag categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List all tag categories and the tags in each. Tag inputs on create_ticket/update_ticket accept a category's ID or name; the list_tickets 'tags' filter needs the ID (tagCategoryId_tagText).")]
    public Task<string> ListTagCategories(
        [Description("true to re-read the tags from the API, unless they were read in the last 30 seconds.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            IReadOnlyList<TagCategory> items = await _cache.GetTagCategoriesAsync(_client, refresh, cancellationToken);
            return new PageResult<TagCategory>(items, items.Count, null, null, null);
        });
    }

    /// <summary>One part of the instance settings, with the instance's identity so the agent knows what it's looking at.</summary>
    private sealed record InstanceSection(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName)
    {
        public InstanceSection(Instance instance)
            : this(instance.Id, instance.DisplayName ?? instance.InstanceName)
        {
        }

        [JsonPropertyName("customFields")] public IReadOnlyList<CustomField>? CustomFields { get; init; }
        [JsonPropertyName("customFieldsLeft")] public IReadOnlyList<CustomField>? CustomFieldsLeft { get; init; }
        [JsonPropertyName("customFieldsRight")] public IReadOnlyList<CustomField>? CustomFieldsRight { get; init; }
        [JsonPropertyName("optionalFieldsLeft")] public IReadOnlyList<CustomField>? OptionalFieldsLeft { get; init; }
        [JsonPropertyName("optionalFieldsRight")] public IReadOnlyList<CustomField>? OptionalFieldsRight { get; init; }
        [JsonPropertyName("assignees")] public AssigneeConfig? Assignees { get; init; }
        [JsonPropertyName("isAutomaticAssignTickets")] public bool? IsAutomaticAssignTickets { get; init; }
        [JsonPropertyName("workflows")] public JsonElement? Workflows { get; init; }
        [JsonPropertyName("sla")] public JsonElement? Sla { get; init; }
    }
}
