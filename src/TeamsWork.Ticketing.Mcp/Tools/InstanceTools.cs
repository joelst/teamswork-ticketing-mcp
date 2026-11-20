using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        "The help desk's settings: custom fields (IDs, types, options, mandatory), the assignee list, workflow states and " +
        "transitions, and SLA rules. Pass 'section' for just one part. Cached for a few minutes; refresh=true after a change.")]
    public Task<string> GetInstance(
        [Description("all (default), customFields, assignees, workflows, or sla.")] string? section = null,
        [Description("true to re-read the settings (at most once every 30 seconds).")] bool refresh = false,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string part = ToolValidation.OptionalEnum(section, "section", Sections) ?? "all";
            Instance instance = await _cache.GetInstanceAsync(_client, timezoneOffset, refresh, cancellationToken);
            bool all = part == "all";
            return new InstanceSection(instance.Id, instance.DisplayName ?? instance.InstanceName)
            {
                Code = all ? instance.Code : null,
                // The API repeats every custom field in the form's left and right column lists; one copy each is enough.
                CustomFields = all || part == "customFields"
                    ? DistinctFields((instance.CustomFields ?? []).Concat(instance.CustomFieldsLeft ?? []).Concat(instance.CustomFieldsRight ?? []))
                    : null,
                OptionalFields = all || part == "customFields"
                    ? DistinctFields((instance.OptionalFieldsLeft ?? []).Concat(instance.OptionalFieldsRight ?? []))
                    : null,
                Assignees = all || part == "assignees" ? Assignees(instance.Assignees) : null,
                IsAutomaticAssignTickets = all || part == "assignees" ? instance.IsAutomaticAssignTickets : null,
                Workflows = all || part == "workflows" ? WorkflowStatus.Compact(instance.Workflows) : null,
                Sla = all || part == "sla" ? instance.Sla : null,
            };
        });
    }

    [McpServerTool(Name = "list_tag_categories", Title = "List tag categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The tag categories and the tags in each. Writes take a tag by its text (and category, when the text is in more than " +
        "one); the list_tickets 'tags' filter needs tagCategoryId_tagText.")]
    public Task<string> ListTagCategories(
        [Description("true to re-read the tags (at most once every 30 seconds).")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            IReadOnlyList<TagCategory> categories = await _cache.GetTagCategoriesAsync(_client, refresh, cancellationToken);
            // Deleted categories and tags can't be used, and colours mean nothing to an agent.
            List<TagCategoryView> items = categories
                .Where(c => c.Deleted != true && c.Id is not null)
                .Select(c => new TagCategoryView(c.Id!, c.Text, (c.Tags ?? []).Where(t => t.Deleted != true && t.Text is not null).Select(t => t.Text!).ToList()))
                .ToList();
            return new PageResult<TagCategoryView>(items, items.Count, null, null, null);
        });
    }

    private static List<CustomField>? DistinctFields(IEnumerable<CustomField> fields)
    {
        List<CustomField> list = fields.DistinctBy(f => f.Id ?? f.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count > 0 ? list : null;
    }

    private static JsonObject? Assignees(AssigneeConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var o = new JsonObject();
        if (config.Type is string type)
        {
            o["type"] = type;
        }

        o["people"] = new JsonArray((config.Peoples ?? [])
            .Select(p => TicketViews.Person(p.Name, p.Email))
            .OfType<string>()
            .Select(p => (JsonNode?)JsonValue.Create(p))
            .ToArray());
        return o;
    }

    private sealed record TagCategoryView(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

    /// <summary>The instance settings asked for, with the instance's identity so the agent knows what it's looking at.</summary>
    private sealed record InstanceSection(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName)
    {
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("customFields")] public IReadOnlyList<CustomField>? CustomFields { get; init; }
        [JsonPropertyName("optionalFields")] public IReadOnlyList<CustomField>? OptionalFields { get; init; }
        [JsonPropertyName("assignees")] public JsonObject? Assignees { get; init; }
        [JsonPropertyName("isAutomaticAssignTickets")] public bool? IsAutomaticAssignTickets { get; init; }
        [JsonPropertyName("workflows")] public JsonNode? Workflows { get; init; }
        [JsonPropertyName("sla")] public JsonElement? Sla { get; init; }
    }
}
