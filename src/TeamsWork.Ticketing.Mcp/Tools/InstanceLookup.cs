using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Turns the names an agent knows (a person's email, a tag category's name, a custom field's title) into the IDs the
/// API needs, and checks custom field values against their definitions before they're sent. The API reports some bad
/// custom field values as a success that returns no ticket, so catching them here gives the agent something to fix.
/// Instance settings and tags are read through <see cref="InstanceCache"/>, and only when a lookup needs them.
/// </summary>
internal sealed partial class InstanceLookup
{
    private static readonly HashSet<string> UnusableStatuses = new(StringComparer.OrdinalIgnoreCase) { "hidden", "deleted", "inactive", "disabled", "archived" };

    private readonly TicketingClient _client;
    private readonly InstanceCache _cache;
    private readonly int? _timezoneOffset;
    private readonly CancellationToken _cancellationToken;

    public InstanceLookup(TicketingClient client, InstanceCache cache, int? timezoneOffset, CancellationToken cancellationToken)
    {
        _client = client;
        _cache = cache;
        _timezoneOffset = timezoneOffset;
        _cancellationToken = cancellationToken;
    }

    public async Task<TicketUser?> PersonAsync(UserRef? person, string paramName)
    {
        if (person is null)
        {
            return null;
        }

        // A complete reference is used as given, so people outside the assignee list (any requestor) still work.
        if (person.IsComplete)
        {
            return person.ToTicketUser(paramName);
        }

        return MatchPerson(person, paramName, await InstanceAsync());
    }

    public async Task<List<TicketTag>?> TagsAsync(IReadOnlyList<TagRef>? tags)
    {
        if (tags is null)
        {
            return null;
        }

        if (tags.Count == 0)
        {
            return [];
        }

        IReadOnlyList<TagCategory> categories = await _cache.GetTagCategoriesAsync(_client, refresh: false, _cancellationToken);
        return tags.Select((t, i) => MatchTag(t, i, categories)).ToList();
    }

    public async Task<JsonElement?> CustomFieldsAsync(Dictionary<string, JsonElement>? customFields)
    {
        if (customFields is null || customFields.Count == 0)
        {
            return null;
        }

        return CheckCustomFields(customFields, await InstanceAsync());
    }

    private Task<Instance> InstanceAsync() => _cache.GetInstanceAsync(_client, _timezoneOffset, refresh: false, _cancellationToken);

    // ---- Matching (pure, so it can be tested without an API) ------------------------------------------------------

    /// <summary>
    /// Finds the one assignee that matches every field supplied. Name falls back to a partial match when nothing
    /// matches exactly, so "Jane" finds "Jane Doe" if she is the only Jane.
    /// </summary>
    internal static TicketUser MatchPerson(UserRef person, string paramName, Instance instance)
    {
        string? id = Blank(person.Id);
        string? name = Blank(person.Name);
        string? email = Blank(person.Email);
        if (id is null && name is null && email is null)
        {
            throw new McpException($"'{paramName}' needs an email, a name, or all of id, name, and email.");
        }

        List<Persona> people = (instance.Assignees?.Peoples ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Email))
            .ToList();

        bool Matches(Persona p, bool partialName) =>
            (id is null || Same(p.Id, id)) &&
            (email is null || Same(p.Email, email)) &&
            (name is null || (partialName ? p.Name!.Contains(name, StringComparison.OrdinalIgnoreCase) : Same(p.Name, name)));

        List<Persona> found = people.Where(p => Matches(p, partialName: false)).ToList();
        if (found.Count == 0 && name is not null)
        {
            found = people.Where(p => Matches(p, partialName: true)).ToList();
        }

        string wanted = email ?? name ?? id!;
        return found.Count switch
        {
            1 => new TicketUser(found[0].Id!, found[0].Name!, found[0].Email!),
            0 => throw new McpException(
                $"'{paramName}' '{wanted}' doesn't match anyone in the instance's assignee list. Use a name or email from " +
                "get_instance (section 'assignees'), or pass id, name, and email together for someone outside that list."),
            _ => throw new McpException(
                $"'{paramName}' '{wanted}' matches more than one person: " +
                string.Join("; ", found.Take(5).Select(p => $"{p.Name} <{p.Email}>")) +
                (found.Count > 5 ? $" and {found.Count - 5} more" : "") + ". Use the email address."),
        };
    }

    /// <summary>Accepts a category ID or name, and a tag's text in any case; returns the canonical ID and text.</summary>
    internal static TicketTag MatchTag(TagRef tag, int index, IReadOnlyList<TagCategory> categories)
    {
        string category = ToolValidation.RequireText(tag.TagCategoryId, $"tags[{index}].tagCategoryId", 256);
        string text = ToolValidation.RequireText(tag.Text, $"tags[{index}].text", 256);

        TagCategory? match = categories.FirstOrDefault(c => string.Equals(c.Id, category, StringComparison.Ordinal))
                             ?? categories.FirstOrDefault(c => Same(c.Text, category));
        if (match?.Id is null)
        {
            throw new McpException(
                $"'tags[{index}].tagCategoryId' '{category}' is not a tag category ID or name. Categories: " +
                (categories.Count == 0 ? "(none defined)" : string.Join(", ", categories.Select(c => c.Text).Where(t => t is not null))) + ".");
        }

        List<string> tagTexts = (match.Tags ?? []).Where(t => t.Deleted != true && t.Text is not null).Select(t => t.Text!).ToList();
        string? canonical = tagTexts.FirstOrDefault(t => string.Equals(t, text, StringComparison.Ordinal))
                            ?? tagTexts.FirstOrDefault(t => Same(t, text));
        return canonical is not null
            ? new TicketTag(match.Id, canonical)
            : throw new McpException(
                $"'tags[{index}].text' '{text}' is not a tag in category '{match.Text}'. Tags there: " +
                (tagTexts.Count == 0 ? "(none)" : string.Join(", ", tagTexts.Take(30)) + (tagTexts.Count > 30 ? ", ..." : "")) + ".");
    }

    /// <summary>
    /// Maps each key (a field ID, or a field's title) to a field the instance defines, and checks each value against
    /// the field's type: a string for text and date fields, a boolean for toggles, an array of option keys for lists
    /// (an option's text is accepted and replaced by its key), and an array for people and email pickers. JSON null
    /// clears a field and is always allowed. Fields of a type this server doesn't know are passed through unchecked.
    /// </summary>
    internal static JsonElement CheckCustomFields(Dictionary<string, JsonElement> values, Instance instance)
    {
        // Not the optional fields: those are the form's built-in ones (Requestor, Priority, Expected Date, Tags) with
        // non-GUID IDs, set through their own parameters rather than customFields.
        List<CustomField> fields = new[] { instance.CustomFields, instance.CustomFieldsLeft, instance.CustomFieldsRight }
            .SelectMany(list => list ?? [])
            .Where(f => Guid.TryParse(f.Id, out _))
            .DistinctBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in values)
        {
            CustomField field = fields.FirstOrDefault(f => string.Equals(f.Id, key.Trim(), StringComparison.OrdinalIgnoreCase))
                                ?? SingleByTitle(fields, key)
                                ?? throw new McpException(
                                    $"'customFields' key '{key}' is not a custom field ID or title on this instance. Fields: " +
                                    (fields.Count == 0 ? "(none defined)" : string.Join("; ", fields.Take(30).Select(f => $"{f.Title} ({f.Id})"))) + ".");

            // The API answers a hidden field with "not found or invalid definition".
            if (UnusableStatuses.Contains(field.Status ?? ""))
            {
                throw new McpException(
                    $"Custom field '{field.Title ?? field.Id}' is {field.Status} on this instance, so the API won't accept a value for it. " +
                    "Leave it out, or ask a Ticketing administrator to show the field.");
            }

            result[field.Id!] = CheckValue(field, value);
        }

        return JsonSerializer.SerializeToElement(result, TicketingClient.JsonOptions);
    }

    private static CustomField? SingleByTitle(List<CustomField> fields, string title)
    {
        List<CustomField> matches = fields.Where(f => Same(f.Title, title.Trim())).ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new McpException($"'customFields' key '{title}' is the title of more than one field. Use the field ID: {string.Join(", ", matches.Select(f => f.Id))}."),
        };
    }

    private static JsonElement CheckValue(CustomField field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return value;
        }

        string label = $"Custom field '{field.Title ?? field.Id}'";
        switch (TypeKey(field))
        {
            case "text" or "textarea" or "date":
                return value.ValueKind == JsonValueKind.String
                    ? value
                    : throw new McpException($"{label} takes a string{(TypeKey(field) == "date" ? " date (YYYY-MM-DD)" : "")}.");

            case "toggle":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value
                    : throw new McpException($"{label} is a toggle and takes true or false.");

            case "list":
                return CheckListValue(field, value, label);

            case "peoplepicker" or "emailpicker":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    throw new McpException($"{label} takes an array of people, each {{\"id\",\"name\",\"email\"}}.");
                }

                RequireSingleIfNotMultiple(field, value, label);
                return value;

            default:
                return value;
        }
    }

    private static JsonElement CheckListValue(CustomField field, JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            throw new McpException($"{label} is a list and takes an array of option keys, for example [\"option1\"].");
        }

        RequireSingleIfNotMultiple(field, value, label);

        List<(string Key, string? Text)> options = OptionsOf(field);
        if (options.Count == 0)
        {
            return value; // options in a shape this server doesn't read; leave the check to the API
        }

        var keys = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string chosen = item.GetString()!;
            (string Key, string? Text) option = options.FirstOrDefault(o => string.Equals(o.Key, chosen, StringComparison.Ordinal));
            if (option.Key is null)
            {
                option = options.FirstOrDefault(o => Same(o.Key, chosen) || Same(o.Text, chosen));
            }

            keys.Add(option.Key ?? throw new McpException(
                $"{label} has no option '{chosen}'. Options: {string.Join(", ", options.Select(o => o.Text is null ? o.Key : $"{o.Key} ({o.Text})"))}."));
        }

        return JsonSerializer.SerializeToElement(keys, TicketingClient.JsonOptions);
    }

    private static void RequireSingleIfNotMultiple(CustomField field, JsonElement value, string label)
    {
        if (field.IsMultiple == false && value.GetArrayLength() > 1)
        {
            throw new McpException($"{label} takes a single value; pass an array with one entry.");
        }
    }

    /// <summary>The type key without the numeric prefix the OpenAPI document uses (1_text becomes text).</summary>
    internal static string? TypeKey(CustomField field)
    {
        string? key = field.Type switch
        {
            { ValueKind: JsonValueKind.Object } t when t.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String => k.GetString(),
            { ValueKind: JsonValueKind.String } t => t.GetString(),
            _ => null,
        };

        return key is null ? null : TypePrefix().Replace(key, "").ToLowerInvariant();
    }

    private static List<(string Key, string? Text)> OptionsOf(CustomField field)
    {
        if (field.Options is not { ValueKind: JsonValueKind.Array } options)
        {
            return [];
        }

        var result = new List<(string, string?)>();
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.Object && option.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String)
            {
                string? text = option.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                result.Add((key.GetString()!, text));
            }
        }

        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+_")]
    private static partial Regex TypePrefix();
}
