using System.Globalization;
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
    private readonly IReadOnlySet<string> _externalDomains;
    private Instance? _instance;
    private bool _refreshed;

    public InstanceLookup(TicketingClient client, InstanceCache cache, int? timezoneOffset, CancellationToken cancellationToken, IReadOnlySet<string>? externalDomains = null)
    {
        _client = client;
        _cache = cache;
        _timezoneOffset = timezoneOffset;
        _cancellationToken = cancellationToken;
        _externalDomains = externalDomains ?? new HashSet<string>();
    }

    /// <summary>
    /// Resolves a person with <see cref="ResolvePerson"/>. <paramref name="assigneeOnly"/> is for the assignee field,
    /// which only takes someone from the instance's assignee list.
    /// </summary>
    public async Task<TicketUser?> PersonAsync(UserRef? person, string paramName, bool assigneeOnly)
    {
        if (person is null)
        {
            return null;
        }

        return await WithInstanceAsync(instance => ResolvePerson(person, paramName, instance, assigneeOnly, _externalDomains));
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
        try
        {
            return tags.Select((t, i) => MatchTag(t, i, categories)).ToList();
        }
        catch (McpException ex) when (IsMiss(ex))
        {
            // A tag added since the cache was filled isn't in it yet: read the tags once more before giving up.
            categories = await _cache.GetTagCategoriesAsync(_client, refresh: true, _cancellationToken);
            return tags.Select((t, i) => MatchTag(t, i, categories)).ToList();
        }
    }

    public async Task<JsonElement?> CustomFieldsAsync(Dictionary<string, JsonElement>? customFields)
    {
        if (customFields is null || customFields.Count == 0)
        {
            return null;
        }

        return await WithInstanceAsync(instance => CheckCustomFields(customFields, instance, _externalDomains));
    }

    /// <summary>
    /// Runs a lookup against the cached instance settings. If something isn't found (a person, field, or option added
    /// since the cache was filled), the settings are read once more, for this whole call, before the error stands.
    /// </summary>
    private async Task<T> WithInstanceAsync<T>(Func<Instance, T> resolve)
    {
        // Read once per call, so a call that resolves several things uses one copy (and one request when uncached).
        _instance ??= await _cache.GetInstanceAsync(_client, _timezoneOffset, refresh: false, _cancellationToken);
        try
        {
            return resolve(_instance);
        }
        catch (McpException ex) when (IsMiss(ex) && !_refreshed)
        {
            _refreshed = true;
            _instance = await _cache.GetInstanceAsync(_client, _timezoneOffset, refresh: true, _cancellationToken);
            return resolve(_instance);
        }
    }

    // ---- Matching (pure, so it can be tested without an API) ------------------------------------------------------

    /// <summary>
    /// The one rule for every person an agent names (assignee, requestor, people-picker fields), so a reference can't
    /// pair one person's ID with another's email and have the ticket look assigned to, or approved by, someone else:
    /// <list type="bullet">
    ///   <item>an email or name alone is looked up in the assignee list;</item>
    ///   <item>a complete {id, name, email} whose ID or email belongs to someone in that list must match that person
    ///   (their own name is then used);</item>
    ///   <item>anyone else is accepted as given only where people outside the list make sense (requestors and
    ///   people-picker fields), and refused for the assignee. They may not use the name of someone on the list, and
    ///   when Ticketing:ExternalEmailDomains is set, their email must be in one of those domains.</item>
    /// </list>
    /// </summary>
    internal static TicketUser ResolvePerson(UserRef person, string paramName, Instance instance, bool assigneeOnly, IReadOnlySet<string>? externalDomains = null)
    {
        List<Persona> people = AssigneesOf(instance);
        if (assigneeOnly && people.Count == 0)
        {
            // Nothing to check an assignee against; an empty list doesn't mean anyone goes.
            throw new McpException(
                $"'{paramName}' can't be checked: the instance's assignee list is empty or unreadable, so this server can't assign tickets. " +
                "Assign it in the Ticketing app.");
        }

        if (!person.IsComplete)
        {
            return MatchPerson(person, paramName, instance);
        }

        TicketUser given = person.ToTicketUser(paramName);
        Persona? byId = people.FirstOrDefault(p => Same(p.Id, given.Id));
        Persona? byEmail = people.FirstOrDefault(p => Same(p.Email, given.Email));

        // The email-to-ticket form puts the email in every field, so its ID is no one's object ID.
        bool emailForm = Same(given.Id, given.Email);
        if (byEmail is not null && (byId == byEmail || (byId is null && emailForm)))
        {
            return new TicketUser(byEmail.Id!, byEmail.Name!, byEmail.Email!);
        }

        if (byId is not null || byEmail is not null)
        {
            throw new McpException(
                $"'{paramName}' mixes the ID and email of different people ({(byId ?? byEmail)!.Name} is in the assignee list with other " +
                "details). Use the id, name, and email exactly as get_instance lists them, or just the email.");
        }

        if (assigneeOnly)
        {
            throw Miss(
                $"'{paramName}' '{given.Email}' isn't in the instance's assignee list, and a ticket can only be assigned to someone " +
                "in it. Use a name or email from get_instance (section 'assignees').");
        }

        // Someone outside the list can't appear under a listed person's name.
        if (people.FirstOrDefault(p => Same(p.Name, given.Name)) is Persona namesake)
        {
            throw new McpException(
                $"'{paramName}' uses the name of {namesake.Name}, who is in the assignee list with a different ID and email. Use their " +
                "details from get_instance, or the outside person's own name.");
        }

        string domain = given.Email[(given.Email.LastIndexOf('@') + 1)..];
        if (externalDomains is { Count: > 0 } && !externalDomains.Contains(domain))
        {
            throw new McpException(
                $"'{paramName}' '{given.Email}' is outside the email domains allowed for people not in the assignee list " +
                $"(Ticketing:ExternalEmailDomains: {string.Join(", ", externalDomains.Order(StringComparer.OrdinalIgnoreCase))}).");
        }

        return given;
    }

    /// <summary>
    /// Finds the one assignee that matches every field supplied. A name that matches no one exactly may match the start
    /// of a word in a name, so "Jane" finds "Jane Doe" if she is the only one, but "ane" finds no one.
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

        List<Persona> people = AssigneesOf(instance);

        bool Matches(Persona p, bool partialName) =>
            (id is null || Same(p.Id, id)) &&
            (email is null || Same(p.Email, email)) &&
            (name is null || (partialName ? StartsAWord(p.Name!, name) : Same(p.Name, name)));

        List<Persona> found = people.Where(p => Matches(p, partialName: false)).ToList();
        if (found.Count == 0 && name is not null)
        {
            found = people.Where(p => Matches(p, partialName: true)).ToList();
        }

        string wanted = email ?? name ?? id!;
        return found.Count switch
        {
            1 => new TicketUser(found[0].Id!, found[0].Name!, found[0].Email!),
            0 => throw Miss(
                $"'{paramName}' '{wanted}' doesn't match anyone in the instance's assignee list. Use a name or email from " +
                "get_instance (section 'assignees'), or, for a requestor or people field, pass id, name, and email together for someone outside that list."),
            _ => throw new McpException(
                $"'{paramName}' '{wanted}' matches more than one person: " +
                string.Join("; ", found.Take(5).Select(p => $"{p.Name} <{p.Email}>")) +
                (found.Count > 5 ? $" and {found.Count - 5} more" : "") + ". Use the email address."),
        };
    }

    private static List<Persona> AssigneesOf(Instance instance) =>
        (instance.Assignees?.Peoples ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Email))
            .ToList();

    /// <summary>True when the name, or one of its words, starts with <paramref name="prefix"/>.</summary>
    private static bool StartsAWord(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        name.Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Accepts a category ID or name, and a tag's text in any case; returns the canonical ID and text.</summary>
    internal static TicketTag MatchTag(TagRef tag, int index, IReadOnlyList<TagCategory> categories)
    {
        string category = ToolValidation.RequireText(tag.TagCategoryId, $"tags[{index}].tagCategoryId", 256);
        string text = ToolValidation.RequireText(tag.Text, $"tags[{index}].text", 256);

        TagCategory? match = categories.FirstOrDefault(c => string.Equals(c.Id, category, StringComparison.Ordinal))
                             ?? categories.FirstOrDefault(c => Same(c.Text, category));
        if (match?.Id is null)
        {
            throw Miss(
                $"'tags[{index}].tagCategoryId' '{category}' is not a tag category ID or name. Categories: " +
                (categories.Count == 0 ? "(none defined)" : string.Join(", ", categories.Select(c => c.Text).Where(t => t is not null))) + ".");
        }

        List<string> tagTexts = (match.Tags ?? []).Where(t => t.Deleted != true && t.Text is not null).Select(t => t.Text!).ToList();
        string? canonical = tagTexts.FirstOrDefault(t => string.Equals(t, text, StringComparison.Ordinal))
                            ?? tagTexts.FirstOrDefault(t => Same(t, text));
        return canonical is not null
            ? new TicketTag(match.Id, canonical)
            : throw Miss(
                $"'tags[{index}].text' '{text}' is not a tag in category '{match.Text}'. Tags there: " +
                (tagTexts.Count == 0 ? "(none)" : string.Join(", ", tagTexts.Take(30)) + (tagTexts.Count > 30 ? ", ..." : "")) + ".");
    }

    /// <summary>
    /// Maps each key (a field ID, or a field's title) to a field the instance defines, and checks each value against
    /// the field's type: a length-capped string for text, a YYYY-MM-DD date, a boolean for toggles, option keys for
    /// lists (an option's text is accepted and replaced by its key), and resolved people for people and email pickers.
    /// JSON null clears a field and is always allowed. A field of a type this server can't check is refused, so nothing
    /// unchecked reaches the API.
    /// </summary>
    internal static JsonElement CheckCustomFields(Dictionary<string, JsonElement> values, Instance instance, IReadOnlySet<string>? externalDomains = null)
    {
        List<FieldDefinition> fields = CustomFieldsOf(instance);

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in values)
        {
            FieldDefinition field = fields.FirstOrDefault(f => string.Equals(f.Field.Id, key.Trim(), StringComparison.OrdinalIgnoreCase))
                                    ?? SingleByTitle(fields, key)
                                    ?? throw Miss(
                                        $"'customFields' key '{key}' is not a custom field ID or title on this instance. Fields: " +
                                        (fields.Count == 0 ? "(none defined)" : string.Join("; ", fields.Take(30).Select(f => $"{f.Field.Title} ({f.Field.Id})"))) + ".");

            // The API answers a hidden field with "not found or invalid definition".
            if (field.UnusableStatus is string status)
            {
                throw new McpException(
                    $"Custom field '{field.Field.Title ?? field.Field.Id}' is {status} on this instance, so the API won't accept a value for it. " +
                    "Leave it out, or ask a Ticketing administrator to show the field.");
            }

            if (field.Conflicting)
            {
                throw new McpException(
                    $"Custom field '{field.Field.Title ?? field.Field.Id}' is defined differently in different parts of the instance settings, " +
                    "so its value can't be checked. Ask a Ticketing administrator to review the field.");
            }

            if (result.ContainsKey(field.Field.Id!))
            {
                throw new McpException($"Custom field '{field.Field.Title ?? field.Field.Id}' is given more than once (by ID and by title). Give it once.");
            }

            result[field.Field.Id!] = CheckValue(field.Field, value, instance, externalDomains);
        }

        return JsonSerializer.SerializeToElement(result, TicketingClient.JsonOptions);
    }

    /// <summary>
    /// The custom fields, one per ID. The live API lists custom fields in customFields and again in the
    /// customFieldsLeft/Right columns; the copies can disagree, so a field is unusable if any copy says it is. The
    /// optional fields are left out: they are the form's built-in ones (Requestor, Priority, Expected Date, Tags) with
    /// non-GUID IDs, set through their own parameters.
    /// </summary>
    private static List<FieldDefinition> CustomFieldsOf(Instance instance) =>
        new[] { instance.CustomFields, instance.CustomFieldsLeft, instance.CustomFieldsRight }
            .SelectMany(list => list ?? [])
            .Where(f => Guid.TryParse(f.Id, out _))
            .GroupBy(f => f.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(copies => new FieldDefinition(
                copies.First(),
                copies.Select(c => c.Status).FirstOrDefault(status => status is not null && UnusableStatuses.Contains(status)),
                // Copies that disagree on what the field is leave no definition to check a value against.
                copies.Select(c => (TypeKey(c), c.IsMultiple ?? false)).Distinct().Count() > 1))
            .ToList();

    /// <summary>A title matches usable fields first, so a hidden field doesn't make a visible one of the same name ambiguous.</summary>
    private static FieldDefinition? SingleByTitle(List<FieldDefinition> fields, string title)
    {
        List<FieldDefinition> all = fields.Where(f => Same(f.Field.Title, title.Trim())).ToList();
        List<FieldDefinition> matches = all.Where(f => f.UnusableStatus is null).ToList() is { Count: > 0 } usable ? usable : all;
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new McpException($"'customFields' key '{title}' is the title of more than one field. Use the field ID: {string.Join(", ", matches.Select(f => f.Field.Id))}."),
        };
    }

    private sealed record FieldDefinition(CustomField Field, string? UnusableStatus, bool Conflicting);

    // Matches the limit on descriptions and comments.
    private const int MaxTextLength = 20_000;

    /// <summary>
    /// Checks the value itself, not only its JSON kind, and returns what should be sent: dates in YYYY-MM-DD form,
    /// list options as keys, and people as complete {id, name, email} references.
    /// </summary>
    private static JsonElement CheckValue(CustomField field, JsonElement value, Instance instance, IReadOnlySet<string>? externalDomains)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return value;
        }

        string label = $"Custom field '{field.Title ?? field.Id}'";
        switch (TypeKey(field))
        {
            case "text" or "textarea":
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new McpException($"{label} takes a string.");
                }

                return value.GetString()!.Length <= MaxTextLength
                    ? value
                    : throw new McpException($"{label} is too long (max {MaxTextLength} characters).");

            case "date":
                // The API fails silently on datetimes in date fields, as it does for expectedDate.
                return value.ValueKind == JsonValueKind.String &&
                       DateOnly.TryParseExact(value.GetString()!.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
                    ? JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : throw new McpException($"{label} takes a date in YYYY-MM-DD form (no time), for example 2026-04-01.");

            case "toggle":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value
                    : throw new McpException($"{label} is a toggle and takes true or false.");

            case "list":
                return CheckListValue(field, value, label);

            case "peoplepicker" or "emailpicker":
                return CheckPeopleValue(field, value, label, instance, externalDomains);

            default:
                throw new McpException(
                    $"{label} is of a type this server can't check ({TypeKey(field) ?? "unknown"}), so it can't be set through this tool.");
        }
    }

    /// <summary>
    /// Each entry is resolved by <see cref="ResolvePerson"/>, like a requestor. A bare email string is shorthand for
    /// {email}.
    /// </summary>
    private static JsonElement CheckPeopleValue(CustomField field, JsonElement value, string label, Instance instance, IReadOnlySet<string>? externalDomains)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new McpException($"{label} takes an array of people, each an email or {{\"id\",\"name\",\"email\"}}.");
        }

        RequireSingleIfNotMultiple(field, value, label);
        ToolValidation.MaxCount(value.EnumerateArray().ToList(), label, 50);

        var people = new List<TicketUser>();
        int index = 0;
        foreach (JsonElement entry in value.EnumerateArray())
        {
            string param = $"customFields.{field.Title ?? field.Id}[{index++}]";
            UserRef person = entry.ValueKind switch
            {
                JsonValueKind.String => new UserRef(Email: entry.GetString()),
                JsonValueKind.Object => ReadPerson(entry),
                _ => throw new McpException($"{param} must be an email or {{\"id\",\"name\",\"email\"}}."),
            };

            people.Add(ResolvePerson(person, param, instance, assigneeOnly: false, externalDomains));
        }

        return JsonSerializer.SerializeToElement(people, TicketingClient.JsonOptions);
    }

    private static UserRef ReadPerson(JsonElement entry)
    {
        string? Text(string name) =>
            entry.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // An empty reference is refused by MatchPerson, which names the parameter.
        return new UserRef(Text("id"), Text("name"), Text("email"));
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
            throw new McpException($"{label} has no options this server can read, so a value can't be checked or set through this tool.");
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

            keys.Add(option.Key ?? throw Miss(
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

    private const string MissKey = "TeamsWork.InstanceLookup.Miss";

    /// <summary>A "not found" error, which a lookup against cached settings retries once with fresh settings.</summary>
    private static McpException Miss(string message)
    {
        var ex = new McpException(message);
        ex.Data[MissKey] = true;
        return ex;
    }

    private static bool IsMiss(McpException ex) => ex.Data.Contains(MissKey);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+_")]
    private static partial Regex TypePrefix();
}
