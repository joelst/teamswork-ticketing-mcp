using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// What tools return, shaped for a model that pays for every character: the fields an agent acts on, people as
/// "Name &lt;email&gt;", times to the second, long text cut with a note of how much was left out, and nothing internal
/// (storage fields, workflow graphs, unseen counters, signed download URLs, HTML that wasn't asked for). The API's own
/// objects stay as they are for reading and writing; only output goes through here.
/// </summary>
internal static partial class TicketViews
{
    /// <summary>A text field in a list row (a selected description, say).</summary>
    public const int ListTextChars = 300;

    /// <summary>A text field of a single ticket.</summary>
    public const int DetailTextChars = 1000;

    /// <summary>One activity's text.</summary>
    public const int ActivityTextChars = 600;

    /// <summary>The most a caller can ask to keep of a text (maxChars): enough for the whole of any comment or description.</summary>
    public const int FullTextChars = 20_000;

    /// <summary>One side of a field change, which can be a whole old or new description.</summary>
    public const int ChangeValueChars = 120;

    /// <summary>Unseen updates beyond which a requestor is probably not getting the ticket's notifications.</summary>
    public const int UnseenWarningThreshold = 3;

    // ---- Small values -----------------------------------------------------------------------------------------------

    /// <summary>"Name &lt;email&gt;", or whichever of the two there is.</summary>
    public static string? Person(TicketUser? user) => Person(user?.Name, user?.Email);

    public static string? Person(string? name, string? email)
    {
        string? n = Blank(name);
        string? e = Blank(email);
        return (n, e) switch
        {
            (null, null) => null,
            (null, string only) => only,
            (string only, null) => only,
            (string both, string address) when string.Equals(both, address, StringComparison.OrdinalIgnoreCase) => address,
            _ => $"{n} <{e}>",
        };
    }

    /// <summary>A priority as the words tools take (Low, Medium, Important, Urgent): some responses say 2_Medium.</summary>
    public static string? Priority(string? priority) =>
        Blank(priority) is string p ? NumberPrefix().Replace(p, "") : null;

    /// <summary>A time in UTC to the second; the API sends seven-digit fractions. Anything unparseable is left as is.</summary>
    public static string? Time(string? value) =>
        Blank(value) is not string text
            ? null
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t)
                ? Time(t)
                : text;

    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A resolution as the words tools take; the API sometimes returns an internal ID, which means nothing to an agent.</summary>
    public static string? Resolution(string? resolution) =>
        Blank(resolution) is string r && !Guid.TryParse(r, out _) ? r : null;

    /// <summary>
    /// The text, cut to <paramref name="max"/> characters with a note of how much more there was. Asked for in full
    /// (<see cref="FullTextChars"/>), it is kept as written, indentation and all; otherwise its ends are trimmed.
    /// </summary>
    public static string? Clip(string? text, int max)
    {
        if ((max >= FullTextChars ? (string.IsNullOrWhiteSpace(text) ? null : text) : Blank(text)) is not string t)
        {
            return null;
        }

        return t.Length <= max ? t : $"{t[..max].TrimEnd()} … [{t.Length - max} more characters]";
    }

    /// <summary>The text cut to <paramref name="max"/> characters with an ellipsis, for a title in a row.</summary>
    public static string? Short(string? text, int max) =>
        Blank(text) is not string t ? null : t.Length <= max ? t : t[..max].TrimEnd() + "…";

    /// <summary>
    /// A ticket's SLA state in one word, worst first (rtBreached, frtBreached, rtEscalated, frtEscalated), ok only when all
    /// four flags are there and false, and null when that isn't known: a missing flag could be a breach.
    /// </summary>
    public static string? Sla(Ticket t) =>
        t.IsRtBreached == true ? "rtBreached"
        : t.IsFrtBreached == true ? "frtBreached"
        : t.IsRtEscalated == true ? "rtEscalated"
        : t.IsFrtEscalated == true ? "frtEscalated"
        : t is { IsRtBreached: false, IsFrtBreached: false, IsRtEscalated: false, IsFrtEscalated: false } ? "ok"
        : null;

    /// <summary>
    /// The new part of an emailed comment: the quoted earlier message that follows a reply is cut where its header starts
    /// (an Outlook From/Sent block, "-----Original Message-----", or "On … wrote:").
    /// </summary>
    public static string StripQuoted(string text) => Reply(text, int.MaxValue)!;

    /// <summary>
    /// An emailed comment's new part, cut to <paramref name="max"/> characters, with a note when the quoted earlier
    /// message was cut. The note goes after the cut, so it is never cut itself.
    /// </summary>
    public static string? Reply(string? text, int max)
    {
        if (Blank(text) is not string t)
        {
            return null;
        }

        Match quote;
        try
        {
            quote = QuoteStart().Match(t);
        }
        catch (RegexMatchTimeoutException)
        {
            // Cutting the quote is a nicety: an odd text that takes too long is kept whole (and still clipped).
            return Clip(t, max);
        }

        return !quote.Success || quote.Index == 0
            ? Clip(t, max)
            : Clip(t[..quote.Index].TrimEnd(), max) + QuoteCutNote;
    }

    private const string QuoteCutNote = " [quoted reply cut; list_ticket_activities with maxChars=20000 has it]";

    // ---- Tickets ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A ticket in a list: what triage needs. <paramref name="content"/> adds text fields a 'select' asked for
    /// (description, custom fields, resolution comment); scans that read whole tickets leave them out.
    /// </summary>
    /// <param name="textChars">How much of each text value to keep in a custom field.</param>
    public static JsonObject Row(Ticket t, bool content = false, JsonElement? workflows = null, int textChars = ListTextChars)
    {
        var o = new JsonObject();
        Number(o, t);
        Add(o, "title", t.Title);
        Status(o, t, workflows);
        Add(o, "priority", Priority(t.Priority));
        Add(o, "assignee", Person(t.Assignee));
        Add(o, "requestor", Person(t.Requestor));
        Add(o, "createdOn", Time(t.CreatedOn));
        Add(o, "lastUpdatedOn", Time(t.LastUpdatedOn));
        Add(o, "expectedDate", t.ExpectedDate);
        Add(o, "tags", Tags(t.Tags));
        SlaFlags(o, t);
        if (content)
        {
            Add(o, "description", Clip(t.Description, ListTextChars));
            Add(o, "resolution", Resolution(t.Resolution));
            Add(o, "lastResolutionComment", Clip(t.LastResolutionComment, ListTextChars));
            Add(o, "createdBy", Person(t.CreatedBy));
            Add(o, "lastUpdatedBy", Person(t.LastUpdatedBy));
            Add(o, "firstResponseOn", Time(t.FirstResponseOn));
            Add(o, "firstResolutionOn", Time(t.FirstResolutionOn));
            Add(o, "lastResolutionOn", Time(t.LastResolutionOn));
            if (t.CustomFields is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } fields)
            {
                o["customFields"] = ClipTexts(JsonNode.Parse(fields.GetRawText()), textChars);
            }
        }

        return o;
    }

    /// <summary>
    /// Custom field values with each text cut like any other (a text field may hold 20,000 characters), the rest as is.
    /// </summary>
    private static JsonNode? ClipTexts(JsonNode? node, int max)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    obj[key] = ClipTexts(obj[key]?.DeepClone(), max);
                }

                return obj;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    array[i] = ClipTexts(array[i]?.DeepClone(), max);
                }

                return array;
            case JsonValue value when value.TryGetValue(out string? text):
                return JsonValue.Create(max >= FullTextChars ? text : Clip(text, max) ?? text);
            default:
                return node;
        }
    }

    /// <summary>
    /// One ticket in full, for acting on it: everything a row has, its description and resolution, whether it is
    /// resolved, and where it can go next. Its UUID is included, in case its number can't be looked up.
    /// </summary>
    public static JsonObject Detail(Ticket t, bool includeHtml, JsonElement? instanceWorkflows = null, int textChars = DetailTextChars)
    {
        JsonObject o = Row(t, content: true, instanceWorkflows, textChars);
        o.Remove("description");
        o.Remove("lastResolutionComment");
        Add(o, "id", t.Id);
        if (WorkflowStatus.IsResolved(t, instanceWorkflows) is bool resolved)
        {
            o["isResolved"] = resolved;
        }

        Add(o, "description", Clip(t.Description, textChars));
        if (includeHtml)
        {
            Add(o, "description_HTML", t.DescriptionHtml);
        }

        Add(o, "lastResolutionComment", Clip(t.LastResolutionComment, textChars));
        Add(o, "origin", t.Origin);

        // Updates the requestor hasn't opened: many means they probably aren't getting the ticket's notifications.
        if (t.Extra?.TryGetValue("requestorUnseenEventCnt", out JsonElement unseen) == true &&
            unseen.ValueKind == JsonValueKind.Number && unseen.TryGetInt32(out int count))
        {
            o["requestorUnseenUpdates"] = count;
            if (count > UnseenWarningThreshold)
            {
                o["warnings"] = new JsonArray($"The requestor hasn't seen the last {count} updates, so they may not be getting this ticket's notifications.");
            }
        }

        if (WorkflowStatus.AllowedNext(t, instanceWorkflows) is { } next)
        {
            o["allowedNextStatuses"] = ToolJson.ToNode(next);
        }

        return o;
    }

    // ---- Activities and attachments ---------------------------------------------------------------------------------

    /// <summary>
    /// One activity: who did what and when, its text without the quoted earlier email, field changes cut short, and
    /// attachment names. The plain text is enough unless HTML was asked for. Its kind is reply or note for a comment,
    /// automation for an automation rule's work, and otherwise what the API says happened (created, assigned, started,
    /// resolved, closed, reopened...), or change or event when it doesn't say.
    /// </summary>
    /// <param name="maxChars">How much text to keep; <see cref="FullTextChars"/> asks for all of it, quoted reply included.</param>
    public static JsonObject Activity(Activity a, bool includeHtml, int maxChars = ActivityTextChars)
    {
        var o = new JsonObject();
        Add(o, "id", a.Id);
        Add(o, "on", Time(a.When));
        Add(o, "by", Person(a.Author));
        string? text = maxChars >= FullTextChars ? Clip(a.Comment, maxChars) : Reply(a.Comment, maxChars);
        bool changes = a.Changes is { Count: > 0 };
        string? action = Blank(a.Action) is string act && act.Length <= 40 ? act : null;
        Add(o, "kind",
            a.IsAutomation ? "automation"
            : action is null || string.Equals(action, "commented", StringComparison.OrdinalIgnoreCase)
                ? text is not null ? (a.IsPrivate == true ? "note" : "reply") : changes ? "change" : "event"
            : action);
        if (a.IsPrivate == true)
        {
            o["isPrivate"] = true;
        }

        Add(o, "text", text);
        if (includeHtml)
        {
            Add(o, "comment_HTML", a.CommentHtml);
        }

        if (changes)
        {
            var list = new JsonArray();
            foreach (ActivityChange c in a.Changes!)
            {
                var change = new JsonObject();
                Add(change, "field", c.Name);
                // Short by default; whole when the full text is asked for, as a description edit can differ anywhere.
                Add(change, "from", Clip(ChangeValue(c.OldValue), maxChars >= FullTextChars ? FullTextChars : ChangeValueChars));
                Add(change, "to", Clip(ChangeValue(c.NewValue), maxChars >= FullTextChars ? FullTextChars : ChangeValueChars));
                list.Add(change);
            }

            o["changes"] = list;
        }

        if (a.Attachments is { Count: > 0 } files)
        {
            o["attachments"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f.Filename ?? f.Caption ?? "attachment")).ToArray());
        }

        return o;
    }

    /// <summary>
    /// An attachment without its download link: those are signed, short-lived, and long, so they are returned only when
    /// asked for. A hyperlink attachment's URL is its content, so it is kept.
    /// </summary>
    public static JsonObject Attachment(Attachment a, bool includeUrl)
    {
        var o = new JsonObject();
        Add(o, "id", a.Id);
        Add(o, "name", a.Filename ?? a.Caption);
        if (a.Caption is not null && a.Filename is not null && !string.Equals(a.Caption, a.Filename, StringComparison.Ordinal))
        {
            Add(o, "caption", a.Caption);
        }

        if (a.IsHyperlink == true)
        {
            o["isLink"] = true;
        }

        if (a.IsImage == true)
        {
            o["isImage"] = true;
        }

        Add(o, "by", a.CreatedBy);
        Add(o, "on", Time(a.CreatedDateTime));
        if (a.IsHyperlink == true || includeUrl)
        {
            Add(o, "url", a.Src);
        }

        return o;
    }

    // ---- Writes -----------------------------------------------------------------------------------------------------

    /// <summary>The acknowledgement every write returns: which ticket, what changed, and who it was recorded as.</summary>
    public static WriteAck Ack(Ticket? ticket, ActingUser actor, Dictionary<string, object?>? changed = null, string? activityId = null, string? warning = null) =>
        new(true, ticket?.TicketNo, ticket?.TicketNo is null ? ticket?.Id : null, NonNull(changed), activityId,
            Person(actor.Name, actor.Email) ?? actor.Id, warning);

    /// <summary>
    /// An acknowledgement for a comment or attachment, which answers with the activity rather than the ticket. Some answers
    /// carry only the activity's ID, so the ticket the call resolved names it when the answer doesn't.
    /// </summary>
    public static WriteAck Ack(CommentActivity created, ActingUser actor, Guid ticket, Dictionary<string, object?>? changed = null) =>
        new(true, created.TicketNo, created.TicketNo is null ? created.TicketId ?? ticket.ToString("D") : null, NonNull(changed), created.ActivityId,
            Person(actor.Name, actor.Email) ?? actor.Id, null);

    internal sealed record WriteAck(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ticketNo")] int? TicketNo,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("changed")] Dictionary<string, object?>? Changed,
        [property: JsonPropertyName("activityId")] string? ActivityId,
        [property: JsonPropertyName("actedAs")] string ActedAs,
        [property: JsonPropertyName("warning")] string? Warning);

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A ticket's state as its workflow labels it, and its raw value as statusId when that differs: a custom workflow's
    /// ticket carries the state's ID, which says nothing to a reader. update_ticket_status and the status filter take
    /// either.
    /// </summary>
    public static void Status(JsonObject o, Ticket t, JsonElement? workflows)
    {
        string? label = Blank(WorkflowStatus.LabelOf(t, workflows));
        Add(o, "status", label);
        if (Blank(t.Status) is string raw && !string.Equals(raw, label, StringComparison.OrdinalIgnoreCase))
        {
            o["statusId"] = raw;
        }
    }

    /// <summary>
    /// A ticket's tags, from either shape the API uses: a plain text as it is, and a {tagCategoryId, text} tag with its
    /// category kept, so two tags with the same text in different categories can be told apart (removeTags needs it).
    /// </summary>
    public static JsonArray? Tags(JsonElement? tags)
    {
        if (tags is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
        {
            return null;
        }

        var texts = new JsonArray();
        foreach (JsonElement tag in list.EnumerateArray())
        {
            string? text = tag.ValueKind switch
            {
                JsonValueKind.String => tag.GetString(),
                JsonValueKind.Object when tag.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String => t.GetString(),
                _ => null,
            };
            if (Blank(text) is not string value)
            {
                continue;
            }

            if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("tagCategoryId", out JsonElement category) &&
                Blank(category.ValueKind == JsonValueKind.String ? category.GetString() : null) is string categoryId)
            {
                texts.Add(new JsonObject { ["text"] = value, ["tagCategoryId"] = categoryId });
            }
            else
            {
                texts.Add(value);
            }
        }

        return texts.Count > 0 ? texts : null;
    }

    /// <summary>
    /// Tags being written, by text, with the category kept only for a text that is in the list more than once (in two
    /// categories), which would otherwise show as two identical entries.
    /// </summary>
    public static JsonArray? TagList(IEnumerable<TicketTag>? tags)
    {
        if (tags is null)
        {
            return null;
        }

        List<TicketTag> list = tags.ToList();
        var shown = new JsonArray();
        foreach (TicketTag tag in list)
        {
            bool repeated = list.Count(t => string.Equals(t.Text.Trim(), tag.Text.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
            shown.Add(repeated ? new JsonObject { ["text"] = tag.Text, ["tagCategoryId"] = tag.TagCategoryId } : JsonValue.Create(tag.Text));
        }

        return shown;
    }

    private static void Number(JsonObject o, Ticket t)
    {
        if (t.TicketNo is int number)
        {
            o["ticketNo"] = number;
        }
        else
        {
            Add(o, "id", t.Id); // without a number, the UUID is the only way to refer to it
        }
    }

    private static void SlaFlags(JsonObject o, Ticket t)
    {
        if (t.IsFrtBreached is bool frtBreached)
        {
            o["isFrtBreached"] = frtBreached;
        }

        if (t.IsRtBreached is bool rtBreached)
        {
            o["isRtBreached"] = rtBreached;
        }

        if (t.IsFrtEscalated is bool frtEscalated)
        {
            o["isFrtEscalated"] = frtEscalated;
        }

        if (t.IsRtEscalated is bool rtEscalated)
        {
            o["isRtEscalated"] = rtEscalated;
        }
    }

    private static string? ChangeValue(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Object } v when v.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String =>
            Person(n.GetString(), v.TryGetProperty("email", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null),
        { } v => v.GetRawText(),
    };

    private static Dictionary<string, object?>? NonNull(Dictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var kept = values.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        return kept.Count > 0 ? kept : null;
    }

    private static void Add(JsonObject o, string name, string? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }

    private static void Add(JsonObject o, string name, JsonNode? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\d+_")]
    private static partial Regex NumberPrefix();

    // Where the quoted part of an emailed reply starts: a mail header block (a "From:" line, then within a few lines a
    // Sent or Date header and after it a Subject header, as Outlook's replies and forwarded messages have, where a note
    // such as "From: 10.1.1.1 / Sent: 500 requests" has no Subject; with the underscore separator above it if there is
    // one), its original-message
    // banner, or Gmail's "On ... wrote:" when it names an address or quoted lines follow it ("On startup the logger
    // wrote:" in a pasted log does neither). A "From:" line or a "--" line on its own is ordinary text, so it is kept.
    // Email text often ends lines with CRLF, and a multiline $ matches before the \n only, so each $ allows the \r.
    [GeneratedRegex(@"^(?:-{2,}[ \t]*Original Message[ \t]*-{2,}[ \t]*\r?$|On [^\r\n]{0,200}@[^\r\n]{0,200} wrote:[ \t]*\r?$|On [^\r\n]{4,200} wrote:[ \t]*\r?\n(?:[ \t]*\r?\n)?>|(?:_{10,}[ \t]*\r?\n)?From:[ \t][^\r\n]+\r?\n(?:[^\r\n]*\r?\n){0,3}?(?:Sent|Date):[^\r\n]*\r?\n(?:[^\r\n]*\r?\n){0,4}?Subject:)", RegexOptions.Multiline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex QuoteStart();
}
