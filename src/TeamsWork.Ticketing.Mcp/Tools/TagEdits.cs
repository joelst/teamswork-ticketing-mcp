using System.Text.Json;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Adding and removing tags while keeping a ticket's others. The API only replaces the whole list, and writes need each
/// tag's category, but a ticket returns its tags as plain texts; so each is matched back to the one category that has
/// it, and anything uncertain is refused rather than guessed, since a wrong guess would quietly change the ticket.
/// </summary>
internal static class TagEdits
{
    /// <summary>
    /// The ticket's tags with their categories. Tags since deleted from their category still count, so a ticket keeps
    /// them unchanged: they aren't checked again the way new tags are.
    /// </summary>
    public static List<TicketTag> Current(JsonElement? ticketTags, IReadOnlyList<TagCategory> categories)
    {
        if (ticketTags is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return [];
        }

        // The edit replaces the whole list, so a value of another shape (a single tag object, say) is refused rather than
        // read as no tags, which would erase them.
        if (ticketTags is not { ValueKind: JsonValueKind.Array } list)
        {
            throw new McpException("The ticket's tags are in a form this server doesn't recognise, so they can't be kept for certain. Pass the whole list in 'tags' instead.");
        }

        var current = new List<TicketTag>();
        foreach (JsonElement tag in list.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.Object &&
                tag.TryGetProperty("tagCategoryId", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                tag.TryGetProperty("text", out JsonElement written) && written.ValueKind == JsonValueKind.String)
            {
                current.Add(new TicketTag(id.GetString()!, written.GetString()!));
                continue;
            }

            if (tag.ValueKind != JsonValueKind.String)
            {
                throw new McpException("The ticket's tags are in a form this server doesn't recognise, so they can't be kept for certain. Pass the whole list in 'tags' instead.");
            }

            string text = tag.GetString()!;
            var owners = categories.Where(c => c.Id is not null && (c.Tags ?? []).Any(t => Same(t.Text, text))).ToList();
            current.Add(owners.Count switch
            {
                1 => new TicketTag(owners[0].Id!, text),
                0 => throw InstanceLookup.TagMiss(
                    $"The ticket has the tag '{text}', which no tag category lists, so it can't be kept for certain. Pass the whole list in 'tags' instead."),
                _ => throw new McpException(
                    $"The ticket has the tag '{text}', which is in more than one category ({string.Join(", ", owners.Select(c => c.Text ?? c.Id))}), so " +
                    "which one is meant isn't known. Pass the whole list in 'tags' instead (list_tag_categories lists the categories)."),
            });
        }

        return current;
    }

    /// <summary>
    /// The ticket's tags with <paramref name="add"/> added and <paramref name="remove"/> taken off. A removeTags entry
    /// matches by text, and by category too when it names one; one that matches nothing is reported, not refused.
    /// </summary>
    public static (List<TicketTag> Tags, string? Warning) Apply(
        List<TicketTag> current, IReadOnlyList<TicketTag> add, IReadOnlyList<TagRef> remove, IReadOnlyList<TagCategory> categories)
    {
        var removals = remove.Select((r, i) =>
        {
            string text = ToolValidation.RequireText(r.Text, $"removeTags[{i}].text", 256);
            string? category = string.IsNullOrWhiteSpace(r.TagCategoryId) ? null : CategoryId(r.TagCategoryId.Trim(), $"removeTags[{i}].tagCategoryId", categories);
            return (Text: text, Category: category);
        }).ToList();

        bool Removes((string Text, string? Category) r, TicketTag t) =>
            Same(r.Text, t.Text) && (r.Category is null || string.Equals(r.Category, t.TagCategoryId, StringComparison.Ordinal));

        // Without a category, a text the ticket has in two categories names two tags; which one is meant isn't guessed.
        foreach ((string text, _) in removals.Where(r => r.Category is null))
        {
            List<string> owners = current.Where(t => Same(text, t.Text))
                .Select(t => t.TagCategoryId).Distinct(StringComparer.Ordinal).ToList();
            if (owners.Count > 1)
            {
                throw new McpException(
                    $"The ticket has '{text}' in more than one category ({string.Join(", ", owners.Select(id => categories.FirstOrDefault(c => c.Id == id)?.Text ?? id))}); " +
                    "pass removeTags with its tagCategoryId to say which.");
            }
        }

        if (add.FirstOrDefault(a => removals.Any(r => Removes(r, a))) is TicketTag both)
        {
            throw new McpException($"'{both.Text}' is in both addTags and removeTags; pass it in one of them.");
        }

        List<string> notOnTicket = removals.Where(r => !current.Any(t => Removes(r, t))).Select(r => r.Text).ToList();
        var tags = current.Where(t => !removals.Any(r => Removes(r, t))).ToList();
        foreach (TicketTag tag in add)
        {
            if (!tags.Any(t => Same(t.Text, tag.Text) && string.Equals(t.TagCategoryId, tag.TagCategoryId, StringComparison.Ordinal)))
            {
                tags.Add(tag);
            }
        }

        string? warning = notOnTicket.Count == 0
            ? null
            : $"The ticket didn't have {string.Join(", ", notOnTicket.Select(t => $"'{t}'"))}, so nothing was removed for {(notOnTicket.Count == 1 ? "it" : "them")}.";
        return (tags, warning);
    }

    /// <summary>The category <paramref name="given"/> names, by ID or by name; null when none does.</summary>
    internal static TagCategory? FindCategory(string given, IReadOnlyList<TagCategory> categories) =>
        categories.FirstOrDefault(c => string.Equals(c.Id, given, StringComparison.Ordinal))
        ?? categories.FirstOrDefault(c => Same(c.Text, given));

    private static string CategoryId(string given, string paramName, IReadOnlyList<TagCategory> categories) =>
        FindCategory(given, categories)?.Id
        ?? throw new McpException(
            $"'{paramName}' '{given}' is not a tag category ID or name. Categories: " +
            (categories.Count == 0 ? "(none defined)" : string.Join(", ", categories.Select(c => c.Text).OfType<string>())) + ".");

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
