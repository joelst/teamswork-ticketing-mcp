using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

[McpServerToolType]
public sealed class TicketTools
{
    /// <summary>What a list row shows (see <see cref="TicketViews.Row"/>); 'select' adds to it.</summary>
    private const string RowFields = "id,ticketId,title,status,priority,assignee,requestor,createdOn,lastUpdatedOn,expectedDate,tags";

    /// <summary>
    /// The description of list_tickets 'status'. The server replaces it with one naming the instance's own states when it
    /// can read them (see <see cref="StatusDescriptions"/>), so the agent knows the states without asking.
    /// </summary>
    internal const string StatusText =
        "Only tickets in exactly this workflow state (Open, In Progress, Reopened, Resolved, Closed, or a custom state's label " +
        "or ID). For every open or every closed ticket, use isResolved instead.";

    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;

    public TicketTools(TicketingClient client, IActingUserProvider actingUser, InstanceCache cache, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_tickets", Title = "List tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search and list tickets, newest first by default, with filters, sorting and paging. Rows are summaries; 'select' adds " +
        "fields such as description, and get_ticket_brief has everything. For someone's queue use get_queue.")]
    public Task<string> ListTickets(
        [Description("Full-text search across ticket content.")] string? search = null,
        [Description("Exact ticket title.")] string? title = null,
        [Description(StatusText)] string? status = null,
        [Description("Older name for 'status'.")] string? statusId = null,
        [Description("true: only resolved tickets; false: only unresolved (any open state).")] bool? isResolved = null,
        [Description("Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Comma-separated tagCategoryId_tagText (IDs from list_tag_categories).")] string? tags = null,
        [Description("Sort by: status, ticketNo, title, requestorName, requestorEmail, assigneeName, assigneeEmail, expectedDate, priority, createdDateTime, or lastInteraction.")] string? orderBy = null,
        [Description("ASC or DESC (default DESC).")] string? order = null,
        [Description("Fields to add to each row, comma-separated: description, customFields, resolution, lastResolutionComment, createdBy, lastUpdatedBy, firstResponseOn, firstResolutionOn, lastResolutionOn, isFrtEscalated, isRtEscalated.")] string? select = null,
        [Description(DateFilterText.CreatedAfter)] string? createdAfter = null,
        [Description(DateFilterText.CreatedBefore)] string? createdBefore = null,
        [Description(DateFilterText.ExpectedDateAfter)] string? expectedDateAfter = null,
        [Description(DateFilterText.ExpectedDateBefore)] string? expectedDateBefore = null,
        [Description(DateFilterText.LastUpdateAfter)] string? lastUpdateAfter = null,
        [Description(DateFilterText.LastUpdateBefore)] string? lastUpdateBefore = null,
        [Description("Page size (default 20, max 100).")] int? limit = null,
        [Description("Zero-based start for offset paging; ignored with continuationToken.")] int? offset = null,
        [Description("From the previous page, to get the next.")] string? continuationToken = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            // 'status' is the exact state, sent as the API's statusId: the API's own 'status' filter only tells resolved
            // from unresolved, which isResolved already does, and reads as if it were exact ("In Progress" returns every
            // unresolved ticket).
            string? state = ToolValidation.OptionalText(status, "status", 128);
            string? olderName = ToolValidation.OptionalText(statusId, "statusId", 128);
            if (state is not null && olderName is not null && !string.Equals(state, olderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException("'status' and 'statusId' name different states; pass only 'status'.");
            }

            string? extra = ToolValidation.OptionalSelect(select);
            StateFilter? filter = (state ?? olderName) is string wanted ? await StateFilterAsync(wanted, timezoneOffset, cancellationToken) : null;
            var query = new TicketListQuery
            {
                Search = ToolValidation.OptionalText(search, "search", 500),
                Title = ToolValidation.OptionalText(title, "title", 500),
                StatusId = filter?.StatusId,
                IsResolved = isResolved,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                Tags = ToolValidation.OptionalText(tags, "tags", 2000),
                OrderBy = ToolValidation.OptionalOrderBy(orderBy),
                Order = ToolValidation.OptionalEnum(order, "order", ["ASC", "DESC"]),
                Select = extra is null ? RowFields : string.Join(',', (RowFields + "," + extra).Split(',').Distinct(StringComparer.Ordinal)),
                CreatedAfter = ToolValidation.OptionalDateFilter(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateFilter(createdBefore, "createdBefore"),
                ExpectedDateAfter = ToolValidation.OptionalDateFilter(expectedDateAfter, "expectedDateAfter"),
                ExpectedDateBefore = ToolValidation.OptionalDateFilter(expectedDateBefore, "expectedDateBefore"),
                LastUpdateAfter = ToolValidation.OptionalDateFilter(lastUpdateAfter, "lastUpdateAfter"),
                LastUpdateBefore = ToolValidation.OptionalDateFilter(lastUpdateBefore, "lastUpdateBefore"),
                Limit = ToolValidation.ResolvePageSize(limit, _options.DefaultPageSize, _options.MaxPageSize),
                Offset = ToolValidation.OptionalOffset(offset),
                ContinuationToken = ToolValidation.OptionalToken(continuationToken, "continuationToken"),
                TimezoneOffset = timezoneOffset,
            };

            ListResponse<Ticket> r = await _client.ListTicketsAsync(query, cancellationToken);
            IReadOnlyList<Ticket> items = r.Items ?? [];
            _cache.Numbers.Remember(items);
            string? hint = string.Join(" ", new[]
            {
                isResolved is null ? filter?.Hint : null,
                r.ContinuationToken is null ? null : "More results are available: call again with the same filters and this continuationToken.",
            }.OfType<string>());
            List<JsonObject> rows = items.Select(t => TicketViews.Row(t, content: extra is not null)).ToList();
            return new PageResult<JsonObject>(rows, rows.Count, r.ItemCount, r.ContinuationToken, hint.Length > 0 ? hint : null);
        });
    }

    [McpServerTool(Name = "create_ticket", Title = "Create ticket", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Create a ticket, recorded as created by the caller. Only 'title' is required; the requestor defaults to the caller. People " +
        "can be given by email or name, tags by text, custom fields by title. Call find_similar_tickets first to avoid a duplicate.")]
    public Task<string> CreateTicket(
        [Description("Short ticket title.")] string title,
        [Description("Plain-text description. Use descriptionHtml instead for formatting.")] string? description = null,
        [Description("HTML description, used when 'description' isn't given. Script, forms, images and styles are removed; link a screenshot with add_ticket_link_attachments.")] string? descriptionHtml = null,
        [Description("Who raised it (default: the caller). An email or name from the assignee list is enough; for anyone else pass id, name and email. For the email-to-ticket flow, put their email in all three.")] UserRef? requestor = null,
        [Description("Assignee: an email or name from the assignee list.")] UserRef? assignee = null,
        [Description("Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Due date, YYYY-MM-DD.")] string? expectedDate = null,
        [Description("Tags to apply.")] IReadOnlyList<TagRef>? tags = null,
        [Description("Custom field values keyed by field ID or title (get_instance section 'customFields'): text or YYYY-MM-DD string, toggle boolean, list option keys or texts, people as {id,name,email} or an email.")] Dictionary<string, JsonElement>? customFields = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            string validTitle = ToolValidation.RequireText(title, "title", 500);
            string? validDescription = ToolValidation.OptionalText(description, "description");
            string? validHtml = validDescription is null ? ToolValidation.OptionalHtml(descriptionHtml, "descriptionHtml") : null;
            string? validPriority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities);
            string? validDate = ToolValidation.OptionalDateOnly(expectedDate, "expectedDate");
            CheckSizes(tags, customFields);

            var lookup = new InstanceLookup(_client, _cache, timezoneOffset, cancellationToken, _options.ExternalEmailDomainSet());
            var write = new TicketWrite
            {
                Title = validTitle,
                Description = validDescription,
                DescriptionHtml = validHtml,
                Requestor = await lookup.PersonAsync(requestor, "requestor", assigneeOnly: false) ?? actor.ToTicketUser(),
                Assignee = await lookup.PersonAsync(assignee, "assignee", assigneeOnly: true),
                Priority = validPriority,
                ExpectedDate = validDate,
                Tags = await lookup.TagsAsync(tags),
                CustomFields = await lookup.CustomFieldsAsync(customFields),
            };

            Ticket created = await _client.CreateTicketAsync(write, actor.ToTicketUser(), includeHtml: false, timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(created);

            // The live API ignores priority on create (the ticket gets the instance default) but honours it on update.
            // The ticket exists from here on, so a failure setting the priority must not look like a failed create:
            // an agent that saw an error would create the ticket again.
            string? warning = null;
            if (validPriority is not null && !string.Equals(TicketViews.Priority(created.Priority), validPriority, StringComparison.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(created.Id, out Guid createdId))
                {
                    warning = $"The ticket was created, but its priority is {TicketViews.Priority(created.Priority) ?? "unset"} rather than {validPriority}, and the " +
                              "API returned no ID to correct it with. Don't create it again; find it with list_tickets and use update_ticket.";
                }
                else
                {
                    // The ticket exists now, so the follow-up doesn't use the caller's token: a caller that gives up here would
                    // otherwise get an error for a ticket that was created, and might create it again. Its own time limit,
                    // the longest the client's retries can take, keeps the call from hanging without cutting a retry short.
                    // Settings that pass validation can make that longer than a timer can wait (about 49 days), so it is
                    // capped there, and the token is made inside the try so nothing here can fail a created ticket.
                    try
                    {
                        using var followUp = new CancellationTokenSource(TicketingClient.TimerLimit(TicketingClient.LongestRequest(_options)));
                        created = await _client.UpdateTicketAsync(createdId, new TicketWrite { Priority = validPriority }, actor.ToTicketUser(), includeHtml: false, timezoneOffset, followUp.Token);
                    }
                    catch (Exception ex)
                    {
                        // Only the API's own messages are written for the agent; anything else stays generic.
                        string reason = ex is TicketingApiException api ? api.Message : ex is OperationCanceledException ? "it took too long." : "the request failed.";
                        string which = created.TicketNo is int no ? $"Ticket #{no}" : $"Ticket {created.Id}";
                        warning = $"{which} was created, but setting its priority to {validPriority} failed: {reason} " +
                                  $"Don't create it again; call update_ticket on it with the priority.";
                    }
                }
            }

            return TicketViews.Ack(created, actor, new Dictionary<string, object?>
            {
                ["status"] = created.Status,
                ["priority"] = TicketViews.Priority(created.Priority),
                ["requestor"] = TicketViews.Person(write.Requestor),
                ["assignee"] = TicketViews.Person(write.Assignee),
            }, warning: warning);
        });
    }

    [McpServerTool(Name = "update_ticket", Title = "Update ticket", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Change fields on a ticket; only those passed change. For its workflow state use update_ticket_status. addTags and " +
        "removeTags keep the ticket's other tags; 'tags' replaces them all.")]
    public Task<string> UpdateTicket(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("New title.")] string? title = null,
        [Description("New plain-text description.")] string? description = null,
        [Description("New HTML description, used when 'description' isn't given. Script, forms, images and styles are removed.")] string? descriptionHtml = null,
        [Description("New requestor: an email or name from the assignee list, or id, name and email for anyone else.")] UserRef? requestor = null,
        [Description("New assignee: an email or name from the assignee list.")] UserRef? assignee = null,
        [Description("Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("New due date, YYYY-MM-DD.")] string? expectedDate = null,
        [Description("The whole new tag list.")] IReadOnlyList<TagRef>? tags = null,
        [Description("Tags to add, keeping the others.")] IReadOnlyList<TagRef>? addTags = null,
        [Description("Tags to remove, keeping the others.")] IReadOnlyList<TagRef>? removeTags = null,
        [Description("Custom field values keyed by field ID or title, checked against the field definitions.")] Dictionary<string, JsonElement>? customFields = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            string? validTitle = ToolValidation.OptionalText(title, "title", 500);
            string? validDescription = ToolValidation.OptionalText(description, "description");
            string? validHtml = validDescription is null ? ToolValidation.OptionalHtml(descriptionHtml, "descriptionHtml") : null;
            string? validPriority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities);
            string? validDate = ToolValidation.OptionalDateOnly(expectedDate, "expectedDate");
            CheckSizes(tags, customFields);
            ToolValidation.MaxCount(addTags, "addTags", MaxTags);
            ToolValidation.MaxCount(removeTags, "removeTags", MaxTags);
            bool editTags = addTags is { Count: > 0 } || removeTags is { Count: > 0 };
            if (editTags && tags is not null)
            {
                throw new McpException("Pass either 'tags' (the whole list) or addTags and removeTags (changes to it), not both.");
            }

            Guid id = await Refs.ResolveAsync(ticket, timezoneOffset, cancellationToken);
            var lookup = new InstanceLookup(_client, _cache, timezoneOffset, cancellationToken, _options.ExternalEmailDomainSet());
            List<TicketTag>? newTags = await lookup.TagsAsync(tags);
            string? tagWarning = null;
            if (editTags)
            {
                // The API replaces the whole list, so the ticket's tags are read and changed here. Another change made
                // between the read and the write would be overwritten, as with any update of the whole list.
                List<TicketTag> toAdd = await lookup.TagsAsync(addTags, "addTags") ?? [];
                Ticket existing = await _client.GetTicketAsync(id, includeHtml: false, timezoneOffset, cancellationToken);
                List<TicketTag> current = await lookup.CurrentTagsAsync(existing.Tags);
                (newTags, tagWarning) = TagEdits.Apply(current, toAdd, removeTags ?? [], await lookup.TagCategoriesAsync());
            }

            var write = new TicketWrite
            {
                Title = validTitle,
                Description = validDescription,
                DescriptionHtml = validHtml,
                Requestor = await lookup.PersonAsync(requestor, "requestor", assigneeOnly: false),
                Assignee = await lookup.PersonAsync(assignee, "assignee", assigneeOnly: true),
                Priority = validPriority,
                ExpectedDate = validDate,
                Tags = newTags,
                CustomFields = await lookup.CustomFieldsAsync(customFields),
            };

            if (write.Title is null && write.Description is null && write.DescriptionHtml is null && write.Requestor is null &&
                write.Assignee is null && write.Priority is null && write.ExpectedDate is null && write.Tags is null && write.CustomFields is null)
            {
                throw new McpException("Nothing to update: pass at least one field to change.");
            }

            Ticket updated = await _client.UpdateTicketAsync(id, write, actor.ToTicketUser(), includeHtml: false, timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(updated);
            return TicketViews.Ack(updated, actor, new Dictionary<string, object?>
            {
                ["title"] = write.Title,
                ["description"] = write.Description is not null || write.DescriptionHtml is not null ? "updated" : null,
                ["requestor"] = TicketViews.Person(write.Requestor),
                ["assignee"] = TicketViews.Person(write.Assignee),
                ["priority"] = write.Priority,
                ["expectedDate"] = write.ExpectedDate,
                ["tags"] = write.Tags?.Select(t => t.Text).ToList(),
                ["customFields"] = customFields is { Count: > 0 } ? customFields.Keys.ToList() : null,
            }, warning: tagWarning);
        });
    }

    // Not idempotent: a status change with a comment records the comment each time.
    [McpServerTool(Name = "update_ticket_status", Title = "Change ticket status", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Move a ticket to another workflow state (resolve, close, reopen...). Pass an allowedNextStatuses 'status' from " +
        "get_ticket_brief; a step the workflow doesn't allow, or one needing a comment without one, is refused. When resolving, " +
        "give a resolution.")]
    public Task<string> UpdateTicketStatus(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("Target state: an allowedNextStatuses 'status' (a state or transition label also works).")] string status,
        [Description("When resolving: fixed, cannotResolve, or cancelled.")] string? resolution = null,
        [Description("Comment recorded with the change, which the requestor can see (some steps require one). For a private note, use add_ticket_comment.")] string? comment = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string target = ToolValidation.RequireText(status, "status", 128);
            string? res = ToolValidation.OptionalEnum(resolution, "resolution", ToolValidation.Resolutions);
            string? note = ToolValidation.OptionalText(comment, "comment");
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            Guid id = await Refs.ResolveAsync(ticket, timezoneOffset, cancellationToken);

            // The ticket's workflow says where it can go, so a step it doesn't allow is refused here with the ones it does,
            // and a label becomes what the API takes. Only a missing ticket stops the change; if the ticket can't be read
            // otherwise, the change is sent as before and the API decides.
            Ticket current;
            try
            {
                current = await _client.GetTicketAsync(id, includeHtml: false, timezoneOffset, cancellationToken);
            }
            catch (TicketingApiException ex) when (ex.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                current = new Ticket();
            }

            target = WorkflowStatus.CheckTarget(current, target, note, await WorkflowStatus.WorkflowsForAsync(current, _cache, _client, timezoneOffset, cancellationToken));
            Ticket updated = await _client.UpdateTicketStatusAsync(id, target, res, note, actor.ToTicketUser(), timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(updated);
            return TicketViews.Ack(updated, actor, new Dictionary<string, object?>
            {
                ["status"] = updated.Status ?? target,
                ["resolution"] = res,
                ["comment"] = note is null ? null : "added",
            });
        });
    }

    [McpServerTool(Name = "assign_ticket", Title = "Assign ticket", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Assign a ticket to someone in the assignee list, by email or name (a unique partial name is enough).")]
    public Task<string> AssignTicket(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("The new assignee's email or name.")] string assignee,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string who = ToolValidation.RequireText(assignee, "assignee", 320);
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            Guid id = await Refs.ResolveAsync(ticket, timezoneOffset, cancellationToken);

            UserRef person = who.Contains('@', StringComparison.Ordinal) ? new UserRef(Email: who) : new UserRef(Name: who);
            TicketUser? resolved = await new InstanceLookup(_client, _cache, timezoneOffset, cancellationToken, _options.ExternalEmailDomainSet()).PersonAsync(person, "assignee", assigneeOnly: true);

            Ticket updated = await _client.UpdateTicketAsync(id, new TicketWrite { Assignee = resolved }, actor.ToTicketUser(), includeHtml: false, timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(updated);
            return TicketViews.Ack(updated, actor, new Dictionary<string, object?> { ["assignee"] = TicketViews.Person(resolved) });
        });
    }

    private TicketRefs Refs => new(_client, _cache, _options);

    /// <summary>The statusId to send for a state named by label or ID, and a hint naming the instance's other states.</summary>
    private sealed record StateFilter(string StatusId, string Hint);

    /// <summary>
    /// The statusId to send for a state named by label or ID. The instance's custom states let a label be turned into
    /// its ID and a typo be refused rather than quietly match nothing; if they can't be read, the value goes as given.
    /// </summary>
    private async Task<StateFilter> StateFilterAsync(string state, int? timezoneOffset, CancellationToken cancellationToken)
    {
        JsonElement? workflows = await WorkflowStatus.InstanceWorkflowsAsync(_cache, _client, timezoneOffset, cancellationToken);

        string id = WorkflowStatus.ResolveFilter(state, workflows, "status");
        List<string> others = WorkflowStatus.StateNames(workflows)
            .Where(s => !string.Equals(s, id, StringComparison.OrdinalIgnoreCase) && !string.Equals(s, state, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new StateFilter(id,
            $"Only tickets in '{state}'. Other states: {string.Join(", ", others)}. isResolved=false lists every open ticket, true every closed one.");
    }

    // Far more than a ticket form has; they only bound the size of one request.
    private const int MaxTags = 50;
    private const int MaxCustomFields = 100;

    private static void CheckSizes(IReadOnlyList<TagRef>? tags, Dictionary<string, JsonElement>? customFields)
    {
        ToolValidation.MaxCount(tags, "tags", MaxTags);
        ToolValidation.MaxCount(customFields, "customFields", MaxCustomFields);
        if (customFields?.Keys.FirstOrDefault(k => k.Length > 256) is string longKey)
        {
            throw new McpException($"'customFields' key '{longKey[..40]}...' is too long to be a field ID or title.");
        }
    }
}
