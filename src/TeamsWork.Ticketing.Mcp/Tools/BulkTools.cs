using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// One change made to many tickets, in two calls: a read-only preview that says what would change on each ticket (and
/// which the workflow won't allow), and a confirmation that takes only the preview's token. Nothing is ever written from
/// a filter without a preview, a ticket changed since the preview is skipped rather than overwritten, and every write is
/// recorded as the caller. Each ticket costs a few API requests, so a batch is capped at <see cref="MaxTickets"/>; if the
/// quota runs out part way, or the call runs long, the confirmation stops and returns a token for the rest.
/// </summary>
[McpServerToolType]
public sealed partial class BulkTools
{
    /// <summary>
    /// Tickets one batch may change. A ticket costs a read and up to three writes when confirmed, against a quota of 100
    /// requests a minute shared by everyone (and 50 for one caller), so this is what fits in about two minutes.
    /// </summary>
    internal const int MaxTickets = 50;

    /// <summary>How long one confirmation works before handing the rest back with a resumeToken.</summary>
    internal static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(20);

    // What a preview reads of each ticket. isCustomWorkflow can't be selected (the API fails), and a status change needs it
    // to send a custom workflow's state ID rather than its label, so a preview that changes status reads whole tickets.
    private const string BulkFields = "id,ticketId,title,status,priority,assignee,tags,expectedDate,lastUpdatedOn,_ts";
    private const int TitleChars = 60;

    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;
    private readonly BulkStore _store;
    private readonly IUpstreamCaller? _caller;
    private readonly ILogger<BulkTools> _logger;

    public BulkTools(
        TicketingClient client,
        IActingUserProvider actingUser,
        InstanceCache cache,
        IOptions<TicketingOptions> options,
        BulkStore store,
        IUpstreamCaller? caller = null,
        ILogger<BulkTools>? logger = null)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
        _options = options.Value;
        _store = store;
        _caller = caller;
        _logger = logger ?? NullLogger<BulkTools>.Instance;
    }

    [McpServerTool(Name = "preview_bulk_update", Title = "Preview a bulk update", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Preview one change to many tickets (up to 50), picked by number or by filter: status, assignee, priority, tags, due " +
        "date, and a comment. Changes nothing; says what would change on each ticket and which the workflow blocks, and returns " +
        "a confirmToken for bulk_update.")]
    public Task<string> PreviewBulkUpdate(
        [Description("The tickets, by number or UUID. Pass these or 'filter'.")] IReadOnlyList<string>? tickets = null,
        [Description("Pick the tickets by filter instead of 'tickets'.")] BulkFilter? filter = null,
        [Description("The change to make on every ticket.")] BulkChanges? set = null,
        [Description("A comment to add to every ticket.")] BulkComment? comment = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            if ((tickets is { Count: > 0 }) == (filter is not null))
            {
                throw new McpException("Pass either 'tickets' (numbers or UUIDs) or 'filter', not both.");
            }

            ToolValidation.MaxCount(tickets, "tickets", MaxTickets);
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            var lookup = new InstanceLookup(_client, _cache, timezoneOffset, cancellationToken, _options.ExternalEmailDomainSet());
            Change change = await ReadChangeAsync(set, comment, lookup, timezoneOffset);
            System.Text.Json.JsonElement? workflows = await WorkflowStatus.InstanceWorkflowsAsync(_cache, _client, timezoneOffset, cancellationToken);

            (List<Ticket> picked, string? pickHint) = filter is not null
                ? await PickByFilterAsync(filter, workflows, change.Status is not null, timezoneOffset, cancellationToken)
                : await PickByRefsAsync(tickets!, change.Status is not null, timezoneOffset, cancellationToken);

            IReadOnlyList<TagCategory> categories = change.AddTags.Count > 0 || change.RemoveTags.Count > 0 ? await lookup.TagCategoriesAsync() : [];
            var items = new List<BulkItem>();
            var rows = new JsonArray();
            var unchanged = new JsonArray();
            int blocked = 0;
            foreach (Ticket t in picked)
            {
                (BulkItem? item, JsonObject row) = await PlanAsync(t, change, workflows, categories, lookup);
                if (row["blocked"] is not null)
                {
                    blocked++;
                    rows.Add(row);
                }
                else if (item is null)
                {
                    unchanged.Add(t.TicketNo is int n ? JsonValue.Create(n) : JsonValue.Create(t.Id));
                }
                else
                {
                    items.Add(item);
                    rows.Add(row);
                }
            }

            var result = new JsonObject
            {
                ["count"] = items.Count,
                ["to"] = change.Describe(),
                ["items"] = rows,
            };
            if (unchanged.Count > 0)
            {
                result["unchanged"] = unchanged;
            }

            if (items.Count > 0)
            {
                var batch = new BulkStore.Batch(Owner(actor), _store.Now + BulkStore.Lifetime, BulkStore.NewBatchId(), change.Plan, items);
                result["confirmToken"] = _store.Add(batch);
                result["expiresAt"] = TicketViews.Time(batch.Expires);
            }

            string?[] hints =
            [
                pickHint,
                blocked > 0 ? $"{blocked} {(blocked == 1 ? "ticket is" : "tickets are")} blocked and won't be changed." : null,
                items.Count > 0
                    ? "Show the user what will change; when they agree, call bulk_update with confirmToken alone."
                    : "Nothing to change, so there is no confirmToken.",
            ];
            result["hint"] = string.Join(" ", hints.OfType<string>());
            return result;
        });
    }

    [McpServerTool(Name = "bulk_update", Title = "Apply a bulk update", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Apply a change previewed with preview_bulk_update, by its confirmToken alone, once the user has agreed. Tickets changed " +
        "since the preview are skipped. Confirming the same token again returns the first result without changing anything.")]
    public Task<string> BulkUpdate(
        [Description("The confirmToken from preview_bulk_update.")] string confirmToken,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string token = ToolValidation.RequireLine(confirmToken, "confirmToken", 64);
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            BulkStore.Batch batch = _store.Find(token)
                ?? throw new McpException("That confirmToken is unknown or has expired: tokens last 15 minutes and don't survive a server restart. Preview the change again with preview_bulk_update.");
            if (!string.Equals(batch.Owner, Owner(actor), StringComparison.Ordinal))
            {
                throw new McpException("That confirmToken was issued to someone else. Preview the change yourself with preview_bulk_update.");
            }

            if (batch.IsDone)
            {
                return batch.Result!;
            }

            if (!batch.TryStart())
            {
                // Another call has it, or it was finished or dropped between the lookup and here.
                throw batch.IsDone ? new McpException("That batch has just been applied; call bulk_update with the same confirmToken to see the result.")
                    : batch.IsEvicted ? new McpException("That confirmToken has expired. Preview the change again with preview_bulk_update.")
                    : new McpException("That batch is being applied by another call right now; wait for it to finish.");
            }

            JsonObject result;
            try
            {
                result = await ApplyAsync(batch, actor, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TicketingApiException or McpException)
            {
                // Unexpected here, since ApplyAsync reports each ticket's failure; the batch is spent either way, so a
                // retry can't apply anything twice.
                batch.Finish(new JsonObject { ["ok"] = false, ["batchId"] = batch.BatchId, ["error"] = "The batch stopped part way. Check the tickets with get_ticket_brief before previewing again." });
                throw;
            }

            batch.Finish(result);
            return result;
        });
    }

    private async Task<JsonObject> ApplyAsync(BulkStore.Batch batch, ActingUser actor, CancellationToken cancellationToken)
    {
        BulkPlan plan = batch.Plan;
        TicketUser who = actor.ToTicketUser();
        var applied = new JsonArray();
        var skipped = new JsonArray();
        var failed = new JsonArray();
        List<BulkItem> remaining = [];
        string? stopped = null;
        long started = Stopwatch.GetTimestamp();

        for (int i = 0; i < batch.Items.Count; i++)
        {
            // Clients give a tool call about 30 seconds (Copilot Cowork, for one), so a long batch hands the rest back.
            if (Stopwatch.GetElapsedTime(started) > TimeBudget)
            {
                remaining.AddRange(batch.Items.Skip(i));
                stopped = "time";
                break;
            }

            BulkItem item = batch.Items[i];
            JsonNode label = item.TicketNo is int no ? JsonValue.Create(no) : JsonValue.Create(item.Id.ToString("D"));
            var done = new List<string>();
            try
            {
                Ticket now;
                try
                {
                    now = await _client.GetTicketAsync(item.Id, includeHtml: false, plan.TimezoneOffset, cancellationToken);
                }
                catch (TicketingApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    skipped.Add(new JsonObject { ["ticketNo"] = label.DeepClone(), ["reason"] = "no longer exists" });
                    continue;
                }

                if (!item.Version.Matches(TicketVersion.Of(now)))
                {
                    skipped.Add(new JsonObject { ["ticketNo"] = label.DeepClone(), ["reason"] = "changedSincePreview" });
                    continue;
                }

                if (item.ChangesAssignee || item.ChangesPriority || item.ChangesExpectedDate || item.Tags is not null)
                {
                    await _client.UpdateTicketAsync(item.Id, new TicketWrite
                    {
                        Assignee = item.ChangesAssignee ? plan.Assignee : null,
                        Priority = item.ChangesPriority ? plan.Priority : null,
                        ExpectedDate = item.ChangesExpectedDate ? plan.ExpectedDate : null,
                        Tags = item.Tags,
                    }, who, includeHtml: false, plan.TimezoneOffset, cancellationToken);
                    done.Add("fields");
                }

                bool noteWithStatus = item.StatusValue is not null && plan.CommentText is not null && !plan.CommentIsPrivate;
                if (item.StatusValue is not null)
                {
                    await _client.UpdateTicketStatusAsync(item.Id, item.StatusValue, plan.Resolution, noteWithStatus ? plan.CommentText : null, who, plan.TimezoneOffset, cancellationToken);
                    done.Add("status");
                }

                if (plan.CommentText is not null && !noteWithStatus)
                {
                    await _client.AddCommentAsync(item.Id, plan.CommentText, null, plan.CommentIsPrivate, who, includeHtml: false, cancellationToken);
                    done.Add("comment");
                }

                applied.Add(label.DeepClone());
            }
            catch (TicketingApiException ex) when (ex.IsRateLimited)
            {
                // The quota ran out. A ticket nothing was done to goes back in the queue with the rest; one part done is
                // reported, since its version has moved and it can't be retried as previewed.
                if (done.Count == 0)
                {
                    remaining.AddRange(batch.Items.Skip(i));
                }
                else
                {
                    failed.Add(new JsonObject { ["ticketNo"] = label.DeepClone(), ["error"] = $"Only partly changed ({string.Join(", ", done)}) before the API quota ran out." });
                    remaining.AddRange(batch.Items.Skip(i + 1));
                }

                stopped = "quota";
                break;
            }
            catch (Exception ex) when (ex is TicketingApiException or McpException)
            {
                string partly = done.Count > 0 ? $" ({string.Join(", ", done)} changed first)" : "";
                failed.Add(new JsonObject { ["ticketNo"] = label.DeepClone(), ["error"] = ex.Message + partly });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up (a client's time limit, say) with a request out: whether it was carried out isn't
                // known. What was done is kept with the batch, so confirming the token again returns it, with a token for
                // the tickets not reached.
                string so = done.Count > 0 ? $"{string.Join(", ", done)} changed; the next change may or may not have been" : "it may or may not have been changed";
                failed.Add(new JsonObject { ["ticketNo"] = label.DeepClone(), ["error"] = $"Interrupted: {so}. Check it with get_ticket_brief." });
                remaining.AddRange(batch.Items.Skip(i + 1));
                stopped = "cancelled";
                break;
            }
        }

        var result = new JsonObject
        {
            ["ok"] = failed.Count == 0 && remaining.Count == 0,
            ["batchId"] = batch.BatchId,
            ["applied"] = applied,
        };
        if (skipped.Count > 0)
        {
            result["skipped"] = skipped;
        }

        if (failed.Count > 0)
        {
            result["failed"] = failed;
        }

        result["actedAs"] = TicketViews.Person(actor.Name, actor.Email) ?? actor.Id;
        if (remaining.Count > 0)
        {
            var rest = new BulkStore.Batch(batch.Owner, _store.Now + BulkStore.Lifetime, batch.BatchId, plan, remaining);
            result["resumeToken"] = _store.Add(rest);
            result["hint"] = stopped switch
            {
                "quota" => $"The API quota ran out with {remaining.Count} tickets to go. Wait a minute, then call bulk_update with resumeToken as the confirmToken.",
                "cancelled" => $"The call was interrupted with {remaining.Count} tickets to go. Check the one marked interrupted, then call bulk_update with resumeToken as the confirmToken.",
                _ => $"Stopped after {TimeBudget.TotalSeconds:0} seconds with {remaining.Count} tickets to go. Call bulk_update with resumeToken as the confirmToken to carry on.",
            };
        }

        _logger.LogInformation("Bulk update {BatchId}: {Applied} applied, {Skipped} skipped, {Failed} failed, {Remaining} left for later.",
            batch.BatchId, applied.Count, skipped.Count, failed.Count, remaining.Count);
        return result;
    }

    // ---- Preview -------------------------------------------------------------------------------------------------

    /// <summary>
    /// What one ticket would get: its planned change, and its row in the preview (what changes from what, or why it is
    /// blocked). Null when nothing would change on it.
    /// </summary>
    private static async Task<(BulkItem? Item, JsonObject Row)> PlanAsync(Ticket t, Change change, System.Text.Json.JsonElement? workflows, IReadOnlyList<TagCategory> categories, InstanceLookup lookup)
    {
        var row = new JsonObject();
        if (t.TicketNo is int no)
        {
            row["ticketNo"] = no;
        }
        else
        {
            row["id"] = t.Id;
        }

        if (TicketViews.Short(t.Title, TitleChars) is string title)
        {
            row["title"] = title;
        }

        var from = new JsonObject();
        string? statusValue = null;
        if (change.Status is string target && !WorkflowStatus.IsIn(t, target, workflows))
        {
            string? publicNote = change.Plan.CommentIsPrivate ? null : change.Plan.CommentText;
            try
            {
                statusValue = WorkflowStatus.CheckTarget(t, target, publicNote, workflows);
            }
            catch (McpException ex)
            {
                // A step that records a comment would show a private one to the requestor: refused, not made public.
                bool privateNoteWouldDo = change.Plan.CommentIsPrivate && change.Plan.CommentText is string text && Allows(t, target, text, workflows);
                row["blocked"] = privateNoteWouldDo
                    ? "This step records a comment the requestor can see, and the comment given is private. Change this ticket on its own, or make the comment public."
                    : ex.Message;
                return (null, row);
            }

            from["status"] = t.Status;
        }

        bool assignee = change.Plan.Assignee is TicketUser a && !TicketPeople.Same(t.Assignee, a);
        if (assignee)
        {
            from["assignee"] = TicketViews.Person(t.Assignee) ?? "(unassigned)";
        }

        bool priority = change.Plan.Priority is string p && !string.Equals(TicketViews.Priority(t.Priority), p, StringComparison.OrdinalIgnoreCase);
        if (priority)
        {
            from["priority"] = TicketViews.Priority(t.Priority);
        }

        bool due = change.Plan.ExpectedDate is string d && !string.Equals(t.ExpectedDate?.Trim(), d, StringComparison.Ordinal);
        if (due)
        {
            from["expectedDate"] = t.ExpectedDate ?? "(none)";
        }

        List<TicketTag>? tags = null;
        if (change.AddTags.Count > 0 || change.RemoveTags.Count > 0)
        {
            // A tag this ticket has that can't be matched to one category blocks this ticket, not the whole preview.
            List<TicketTag> current;
            List<TicketTag> next;
            try
            {
                current = await lookup.CurrentTagsAsync(t.Tags);
                (next, _) = TagEdits.Apply(current, change.AddTags, change.RemoveTags, categories);
            }
            catch (McpException ex)
            {
                row["blocked"] = ex.Message;
                return (null, row);
            }

            // By category and text: the same text in another category is another tag.
            static string Key(TicketTag tag) => tag.TagCategoryId.Trim().ToLowerInvariant() + "\u001f" + tag.Text.Trim().ToLowerInvariant();
            if (!next.Select(Key).ToHashSet(StringComparer.Ordinal).SetEquals(current.Select(Key)))
            {
                tags = next;
                from["tags"] = new JsonArray(current.Select(x => (JsonNode?)JsonValue.Create(x.Text)).ToArray());
            }
        }

        if (from.Count == 0 && change.Plan.CommentText is null)
        {
            return (null, row);
        }

        if (from.Count > 0)
        {
            row["from"] = from;
        }

        return (new BulkItem(Guid.Parse(t.Id!), t.TicketNo, TicketVersion.Of(t), statusValue, tags, assignee, priority, due), row);
    }

    private static bool Allows(Ticket t, string target, string comment, System.Text.Json.JsonElement? workflows)
    {
        try
        {
            WorkflowStatus.CheckTarget(t, target, comment, workflows);
            return true;
        }
        catch (McpException)
        {
            return false;
        }
    }

    /// <summary>The tickets a filter picks, refused when there are more than a batch takes.</summary>
    private async Task<(List<Ticket> Tickets, string? Hint)> PickByFilterAsync(BulkFilter filter, System.Text.Json.JsonElement? workflows, bool wholeTickets, int? timezoneOffset, CancellationToken cancellationToken)
    {
        Regex? titlePattern = ToolValidation.OptionalText(filter.TitlePattern, "filter.titlePattern", 500) is string pattern ? Glob(pattern) : null;
        string? assigneeText = ToolValidation.OptionalText(filter.Assignee, "filter.assignee", 320);
        Func<Ticket, bool> assigneeMatches = _ => true;
        if (string.Equals(assigneeText, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            assigneeMatches = t => string.IsNullOrWhiteSpace(t.Assignee?.Email) && string.IsNullOrWhiteSpace(t.Assignee?.Id);
        }
        else if (assigneeText is not null)
        {
            TicketUser them = await QueueTools.FindPersonAsync(_client, _cache, assigneeText, "filter.assignee", timezoneOffset, cancellationToken);
            assigneeMatches = t => TicketPeople.IsPerson(t.Assignee, them.Id, them.Email);
        }

        string? state = ToolValidation.OptionalText(filter.Status, "filter.status", 128);
        var query = new TicketListQuery
        {
            Search = ToolValidation.OptionalText(filter.Search, "filter.search", 500),
            StatusId = state is null ? null : WorkflowStatus.ResolveFilter(state, workflows, "filter.status"),
            IsResolved = filter.IncludeResolved == true ? null : false,
            Priority = ToolValidation.OptionalEnum(filter.Priority, "filter.priority", ToolValidation.Priorities),
            CreatedAfter = ToolValidation.OptionalDateFilter(filter.CreatedAfter, "filter.createdAfter"),
            CreatedBefore = ToolValidation.OptionalDateFilter(filter.CreatedBefore, "filter.createdBefore"),
            Select = wholeTickets ? null : BulkFields,
            TimezoneOffset = timezoneOffset,
        };
        if (query.Search is null && titlePattern is null && state is null && query.Priority is null && assigneeText is null &&
            query.CreatedAfter is null && query.CreatedBefore is null)
        {
            throw new McpException("'filter' needs at least one condition, so a batch can't take every ticket by accident.");
        }

        TicketScan.Result<Ticket> scan = await TicketScan.RunAsync(
            _client,
            query,
            t =>
            {
                _cache.Numbers.Remember(t);
                return Guid.TryParse(t.Id, out _) && (titlePattern is null || titlePattern.IsMatch(t.Title ?? "")) && assigneeMatches(t) ? Slim(t) : null;
            },
            _options.MaxScanTickets,
            wholeTickets ? WorkloadTools.FullTicketPageSize : TicketScan.MaxApiPageSize,
            cancellationToken);

        if (scan.Matches.Count > MaxTickets)
        {
            throw new McpException($"{scan.Matches.Count} tickets match; a batch takes at most {MaxTickets}. Narrow the filter, or split it (by createdAfter and createdBefore, say).");
        }

        return (scan.Matches, TicketScan.TruncationHint(scan, query, _client.TimeZones));
    }

    /// <summary>
    /// The tickets named, as they stand now. Tickets known by UUID (given, or a number seen in an earlier response) are
    /// read one at a time when there are only a couple; otherwise one read of the open tickets finds most of them,
    /// numbers included, without a lookup each, and the rest are found and read one at a time.
    /// </summary>
    private async Task<(List<Ticket> Tickets, string? Hint)> PickByRefsAsync(IReadOnlyList<string> refs, bool wholeTickets, int? timezoneOffset, CancellationToken cancellationToken)
    {
        var wanted = new List<(string Given, Guid? Id, int? Number)>();
        foreach (string r in refs)
        {
            string given = ToolValidation.RequireText(r, "tickets", 64);
            int? number = TicketRefs.BareNumber(given);
            Guid? known = Guid.TryParse(given, out Guid id) && id != Guid.Empty ? id
                : number is int n && _cache.Numbers.TryGet(n, out Guid cached) ? cached
                : null;
            wanted.Add((given, known, number));
        }

        // A scan of the open tickets is one request (or a few on a very large help desk); reading each ticket is one each.
        bool scanPays = wanted.Any(w => w.Id is null) || wanted.Count > 2;
        TicketScan.Result<Ticket> open = !scanPays ? new TicketScan.Result<Ticket>([], 0, 0, false) : await TicketScan.RunAsync(
            _client,
            new TicketListQuery { IsResolved = false, Select = wholeTickets ? null : BulkFields, TimezoneOffset = timezoneOffset },
            t =>
            {
                _cache.Numbers.Remember(t);
                return wanted.Any(w => (w.Id is Guid g && Guid.TryParse(t.Id, out Guid tid) && tid == g) || (w.Number is int n && t.TicketNo == n)) ? Slim(t) : null;
            },
            _options.MaxScanTickets,
            wholeTickets ? WorkloadTools.FullTicketPageSize : TicketScan.MaxApiPageSize,
            cancellationToken);

        var found = new List<Ticket>();
        var seen = new HashSet<Guid>();
        var refsTool = new TicketRefs(_client, _cache, _options);
        foreach ((string given, Guid? gid, int? number) in wanted)
        {
            Ticket? t = open.Matches.FirstOrDefault(m => (gid is Guid g && Guid.TryParse(m.Id, out Guid tid) && tid == g) || (number is int n && m.TicketNo == n));
            if (t is null)
            {
                Guid id = gid ?? await refsTool.ResolveAsync(given, timezoneOffset, cancellationToken, "tickets");
                t = await _client.GetTicketAsync(id, includeHtml: false, timezoneOffset, cancellationToken);
                _cache.Numbers.Remember(t);
            }

            if (Guid.TryParse(t.Id, out Guid key) && seen.Add(key))
            {
                found.Add(t);
            }
        }

        return (found, null);
    }

    private static async Task<Change> ReadChangeAsync(BulkChanges? set, BulkComment? comment, InstanceLookup lookup, int? timezoneOffset)
    {
        string? status = ToolValidation.OptionalText(set?.Status, "set.status", 128);
        string? resolution = ToolValidation.OptionalEnum(set?.Resolution, "set.resolution", ToolValidation.Resolutions);
        if (resolution is not null && status is null)
        {
            throw new McpException("'set.resolution' goes with 'set.status' (the resolved state).");
        }

        string? assigneeText = ToolValidation.OptionalText(set?.Assignee, "set.assignee", 320);
        TicketUser? assignee = assigneeText is null ? null
            : await lookup.PersonAsync(assigneeText.Contains('@', StringComparison.Ordinal) ? new UserRef(Email: assigneeText) : new UserRef(Name: assigneeText), "set.assignee", assigneeOnly: true);
        ToolValidation.MaxCount(set?.AddTags, "set.addTags", 20);
        ToolValidation.MaxCount(set?.RemoveTags, "set.removeTags", 20);
        List<TicketTag> addTags = await lookup.TagsAsync(set?.AddTags, "set.addTags") ?? [];

        string? text = null;
        bool isPrivate = false;
        if (comment is not null)
        {
            text = ToolValidation.RequireText(comment.Text, "comment.text");
            isPrivate = comment.IsPrivate ?? throw new McpException(
                "'comment.isPrivate' is required: true for a private note to agents, false for a reply the requestor sees.");
        }

        var plan = new BulkPlan(
            status,
            resolution,
            assignee,
            ToolValidation.OptionalEnum(set?.Priority, "set.priority", ToolValidation.Priorities),
            ToolValidation.OptionalDateOnly(set?.ExpectedDate, "set.expectedDate"),
            text,
            isPrivate,
            timezoneOffset);
        if (plan is { Status: null, Assignee: null, Priority: null, ExpectedDate: null, CommentText: null } && addTags.Count == 0 && set?.RemoveTags is not { Count: > 0 })
        {
            throw new McpException("Nothing to change: give 'set' (status, assignee, priority, tags or due date) or a 'comment'.");
        }

        return new Change(plan, status, addTags, set?.RemoveTags ?? []);
    }

    /// <summary>The change asked for, with the tag edits, which are worked out per ticket.</summary>
    private sealed record Change(BulkPlan Plan, string? Status, List<TicketTag> AddTags, IReadOnlyList<TagRef> RemoveTags)
    {
        public JsonObject Describe()
        {
            var to = new JsonObject();
            Put(to, "status", Plan.Status);
            Put(to, "resolution", Plan.Resolution);
            Put(to, "assignee", TicketViews.Person(Plan.Assignee));
            Put(to, "priority", Plan.Priority);
            Put(to, "expectedDate", Plan.ExpectedDate);
            if (AddTags.Count > 0)
            {
                to["addTags"] = new JsonArray(AddTags.Select(t => (JsonNode?)JsonValue.Create(t.Text)).ToArray());
            }

            if (RemoveTags.Count > 0)
            {
                to["removeTags"] = new JsonArray(RemoveTags.Select(t => (JsonNode?)JsonValue.Create(t.Text)).ToArray());
            }

            if (Plan.CommentText is not null)
            {
                to["comment"] = Plan.CommentIsPrivate ? "private note" : "reply the requestor sees";
            }

            return to;
        }

        private static void Put(JsonObject o, string name, string? value)
        {
            if (value is not null)
            {
                o[name] = value;
            }
        }
    }

    /// <summary>What a preview keeps of a picked ticket: whole tickets would otherwise be held, descriptions and all.</summary>
    private static Ticket Slim(Ticket t) => new()
    {
        Id = t.Id,
        TicketNo = t.TicketNo,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        Assignee = t.Assignee,
        Tags = t.Tags,
        ExpectedDate = t.ExpectedDate,
        LastUpdatedOn = t.LastUpdatedOn,
        Ts = t.Ts,
        IsCustomWorkflow = t.IsCustomWorkflow,
        Workflow = t.Workflow,
        ResolvedStatus = t.ResolvedStatus,
    };

    /// <summary>The caller a token belongs to: the person (or service account) acting, and the caller the HTTP server knows.</summary>
    private string Owner(ActingUser actor) => $"{actor.Source}|{actor.Id}|{_caller?.Key}";

    /// <summary>A title pattern: * stands for any text, ? for one character, and case doesn't matter.</summary>
    internal static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

}

/// <summary>Which tickets a bulk update picks. At least one condition is required.</summary>
public sealed record BulkFilter
{
    [Description("Full-text search.")]
    [JsonPropertyName("search")]
    public string? Search { get; init; }

    [Description("Title pattern, * for any text: 'Weekly check:*'.")]
    [JsonPropertyName("titlePattern")]
    public string? TitlePattern { get; init; }

    [Description("Only tickets in exactly this workflow state.")]
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [Description("Low, Medium, Important, or Urgent.")]
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [Description("The assignee's email or name, or unassigned.")]
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [Description(DateFilterText.CreatedAfter)]
    [JsonPropertyName("createdAfter")]
    public string? CreatedAfter { get; init; }

    [Description(DateFilterText.CreatedBefore)]
    [JsonPropertyName("createdBefore")]
    public string? CreatedBefore { get; init; }

    [Description("true to include resolved and closed tickets (default: open ones only).")]
    [JsonPropertyName("includeResolved")]
    public bool? IncludeResolved { get; init; }
}

/// <summary>The change a bulk update makes; only what is given changes.</summary>
public sealed record BulkChanges
{
    [Description("Target workflow state, as update_ticket_status takes it.")]
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [Description("With a resolving status: fixed, cannotResolve, or cancelled.")]
    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [Description("New assignee: an email or name from the assignee list.")]
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [Description("Low, Medium, Important, or Urgent.")]
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [Description("Tags to add, keeping the others.")]
    [JsonPropertyName("addTags")]
    public IReadOnlyList<TagRef>? AddTags { get; init; }

    [Description("Tags to remove, keeping the others.")]
    [JsonPropertyName("removeTags")]
    public IReadOnlyList<TagRef>? RemoveTags { get; init; }

    [Description("New due date, YYYY-MM-DD.")]
    [JsonPropertyName("expectedDate")]
    public string? ExpectedDate { get; init; }
}

/// <summary>A comment a bulk update adds to every ticket.</summary>
public sealed record BulkComment
{
    [Description("The comment text.")]
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [Description("Required: true for a private note to agents, false for a reply the requestor sees.")]
    [JsonPropertyName("isPrivate")]
    public bool? IsPrivate { get; init; }
}
