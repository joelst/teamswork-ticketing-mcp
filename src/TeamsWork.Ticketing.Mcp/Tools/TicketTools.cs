using System.ComponentModel;
using System.Text.Json;
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
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly TicketingOptions _options;

    public TicketTools(TicketingClient client, IActingUserProvider actingUser, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_tickets", Title = "List tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search and list tickets, newest first by default. Supports full-text search, filters (status, priority, resolved, tags, " +
        "date ranges), sorting, field selection, and paging. Returns a page of tickets plus totalCount and, when more pages exist, " +
        "a continuationToken to pass back in. Prefer 'select' to keep responses small when you only need a few fields.")]
    public Task<string> ListTickets(
        [Description("Full-text search across ticket content.")] string? search = null,
        [Description("Filter by exact ticket title.")] string? title = null,
        [Description("Legacy resolved-state filter: Open, Reopened, or In Progress return unresolved tickets; Resolved or Closed return resolved tickets. Use statusId for an exact workflow state.")] string? status = null,
        [Description("Exact workflow state to match. Default workflows use Open, Reopened, In Progress, Resolved, Closed; custom workflows use the state IDs from get_instance.")] string? statusId = null,
        [Description("true = only resolved tickets, false = only unresolved tickets.")] bool? isResolved = null,
        [Description("Filter by priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Comma-separated tag filter in the form tagCategoryId_tagText (IDs from list_tag_categories).")] string? tags = null,
        [Description("Sort field: status, ticketId, title, requestorName, requestorEmail, assigneeName, assigneeEmail, expectedDate, priority, createdDateTime, or lastInteraction.")] string? orderBy = null,
        [Description("Sort direction: ASC or DESC (default DESC).")] string? order = null,
        [Description("Comma-separated fields to return, for example 'id,ticketId,title,status,priority,assignee,createdOn'. Allowed: id, ticketId, title, description, status, requestor, customFields, priority, assignee, expectedDate, resolution, firstResponseOn, firstResolutionOn, lastResolutionOn, createdOn, tags, lastUpdatedOn, isFrtEscalated, isRtEscalated, createdBy, lastUpdatedBy, lastResolutionComment.")] string? select = null,
        [Description("Only tickets created after this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdAfter = null,
        [Description("Only tickets created before this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdBefore = null,
        [Description("Only tickets with an expected date after this datetime.")] string? expectedDateAfter = null,
        [Description("Only tickets with an expected date before this datetime.")] string? expectedDateBefore = null,
        [Description("Only tickets last updated after this datetime.")] string? lastUpdateAfter = null,
        [Description("Only tickets last updated before this datetime.")] string? lastUpdateBefore = null,
        [Description("Page size (default 20, max 100).")] int? limit = null,
        [Description("Zero-based starting position for offset paging (default 0). Ignored when continuationToken is supplied.")] int? offset = null,
        [Description("Token from a previous list_tickets response to fetch the next page.")] string? continuationToken = null,
        [Description("Also return description_HTML (sanitised HTML) alongside the plain-text description.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours, e.g. -5 for US Central Daylight Time. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            var query = new TicketListQuery
            {
                Search = ToolValidation.OptionalText(search, "search", 500),
                Title = ToolValidation.OptionalText(title, "title", 500),
                Status = ToolValidation.OptionalEnum(status, "status", ToolValidation.LegacyStatuses),
                StatusId = ToolValidation.OptionalText(statusId, "statusId", 128),
                IsResolved = isResolved,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                Tags = ToolValidation.OptionalText(tags, "tags", 2000),
                OrderBy = ToolValidation.OptionalEnum(orderBy, "orderBy", ToolValidation.OrderByFields),
                Order = ToolValidation.OptionalEnum(order, "order", ["ASC", "DESC"]),
                Select = ToolValidation.OptionalSelect(select),
                CreatedAfter = ToolValidation.OptionalDateTime(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateTime(createdBefore, "createdBefore"),
                ExpectedDateAfter = ToolValidation.OptionalDateTime(expectedDateAfter, "expectedDateAfter"),
                ExpectedDateBefore = ToolValidation.OptionalDateTime(expectedDateBefore, "expectedDateBefore"),
                LastUpdateAfter = ToolValidation.OptionalDateTime(lastUpdateAfter, "lastUpdateAfter"),
                LastUpdateBefore = ToolValidation.OptionalDateTime(lastUpdateBefore, "lastUpdateBefore"),
                Limit = ToolValidation.ResolvePageSize(limit, _options.DefaultPageSize, _options.MaxPageSize),
                Offset = ToolValidation.OptionalOffset(offset),
                ContinuationToken = ToolValidation.OptionalText(continuationToken, "continuationToken", 4000),
                IncludeHtml = includeHtml,
                TimezoneOffset = timezoneOffset,
            };

            ListResponse<Ticket> r = await _client.ListTicketsAsync(query, cancellationToken);
            IReadOnlyList<Ticket> items = r.Items ?? [];
            string? hint = r.ContinuationToken is null
                ? null
                : "More results are available: call list_tickets again with the same filters and this continuationToken.";
            return new PageResult<Ticket>(items, items.Count, r.ItemCount, r.ContinuationToken, hint);
        });
    }

    [McpServerTool(Name = "get_ticket", Title = "Get ticket", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the full details of one ticket by its UUID (the 'id' field, not the human ticket number). Includes the workflow states and allowed next transitions for update_ticket_status.")]
    public Task<string> GetTicket(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Also return description_HTML.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(() =>
            _client.GetTicketAsync(ToolValidation.RequireGuid(ticketId, "ticketId"), includeHtml, timezoneOffset, cancellationToken));
    }

    [McpServerTool(Name = "create_ticket", Title = "Create ticket", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Create a new ticket. Only 'title' is required; the requestor defaults to the signed-in user. Call get_instance first when you " +
        "need custom field IDs or the assignee list, and list_tag_categories for tag IDs. 'expectedDate' must be a plain YYYY-MM-DD date. " +
        "The ticket is recorded as created by the authenticated caller; the agent cannot choose a different actor.")]
    public Task<string> CreateTicket(
        [Description("Short ticket title.")] string title,
        [Description("Plain-text description (line breaks preserved). Use descriptionHtml instead for formatted content.")] string? description = null,
        [Description("Sanitised HTML description. Ignored when 'description' is also supplied.")] string? descriptionHtml = null,
        [Description("Person raising the ticket. Defaults to the signed-in user. To trigger the email-to-ticket flow, set id, name, and email all to the requestor's email address.")] UserRef? requestor = null,
        [Description("Person to assign the ticket to (from get_instance assignees).")] UserRef? assignee = null,
        [Description("Priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Expected completion date in YYYY-MM-DD form only.")] string? expectedDate = null,
        [Description("Tags to apply.")] IReadOnlyList<TagRef>? tags = null,
        [Description("Custom field values keyed by the 36-character custom field ID from get_instance. Value type depends on the field: string for text/date, boolean for toggle, array of option keys for list, array of {id,name,email} for people picker.")] Dictionary<string, JsonElement>? customFields = null,
        [Description("Return description_HTML in the created ticket.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);

            var ticket = new TicketWrite
            {
                Title = ToolValidation.RequireText(title, "title", 500),
                Description = ToolValidation.OptionalText(description, "description"),
                DescriptionHtml = description is null ? ToolValidation.OptionalText(descriptionHtml, "descriptionHtml") : null,
                Requestor = requestor?.ToTicketUser("requestor") ?? actor.ToTicketUser(),
                Assignee = assignee?.ToTicketUser("assignee"),
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                ExpectedDate = ToolValidation.OptionalDateOnly(expectedDate, "expectedDate"),
                Tags = tags?.Select((t, i) => t.ToTicketTag(i)).ToList(),
                CustomFields = ValidateCustomFields(customFields),
            };

            Ticket created = await _client.CreateTicketAsync(ticket, actor.ToTicketUser(), includeHtml, timezoneOffset, cancellationToken);
            return new WriteResult<Ticket>(created, ActedAs.From(actor));
        });
    }

    [McpServerTool(Name = "update_ticket", Title = "Update ticket", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Update fields on an existing ticket. Only the fields you pass are changed; everything else is left as is. " +
        "To change the workflow state use update_ticket_status instead. 'expectedDate' must be a plain YYYY-MM-DD date.")]
    public Task<string> UpdateTicket(
        [Description("Ticket UUID.")] string ticketId,
        [Description("New title.")] string? title = null,
        [Description("New plain-text description.")] string? description = null,
        [Description("New sanitised HTML description. Ignored when 'description' is also supplied.")] string? descriptionHtml = null,
        [Description("New requestor.")] UserRef? requestor = null,
        [Description("New assignee (from get_instance assignees).")] UserRef? assignee = null,
        [Description("New priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("New expected date in YYYY-MM-DD form only.")] string? expectedDate = null,
        [Description("Replacement tag list.")] IReadOnlyList<TagRef>? tags = null,
        [Description("Custom field values to set, keyed by custom field ID from get_instance.")] Dictionary<string, JsonElement>? customFields = null,
        [Description("Return description_HTML in the updated ticket.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);

            var ticket = new TicketWrite
            {
                Title = ToolValidation.OptionalText(title, "title", 500),
                Description = ToolValidation.OptionalText(description, "description"),
                DescriptionHtml = description is null ? ToolValidation.OptionalText(descriptionHtml, "descriptionHtml") : null,
                Requestor = requestor?.ToTicketUser("requestor"),
                Assignee = assignee?.ToTicketUser("assignee"),
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                ExpectedDate = ToolValidation.OptionalDateOnly(expectedDate, "expectedDate"),
                Tags = tags?.Select((t, i) => t.ToTicketTag(i)).ToList(),
                CustomFields = ValidateCustomFields(customFields),
            };

            if (ticket.Title is null && ticket.Description is null && ticket.DescriptionHtml is null && ticket.Requestor is null &&
                ticket.Assignee is null && ticket.Priority is null && ticket.ExpectedDate is null && ticket.Tags is null && ticket.CustomFields is null)
            {
                throw new McpException("Nothing to update: pass at least one field to change.");
            }

            Ticket updated = await _client.UpdateTicketAsync(id, ticket, actor.ToTicketUser(), includeHtml, timezoneOffset, cancellationToken);
            return new WriteResult<Ticket>(updated, ActedAs.From(actor));
        });
    }

    [McpServerTool(Name = "update_ticket_status", Title = "Change ticket status", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Move a ticket to another workflow state (for example resolve, close, or reopen it). The target must be one of the allowed " +
        "next steps for the ticket's current state: check the 'workflow' and 'status' fields from get_ticket first. For default workflows " +
        "use Open, Reopened, In Progress, Resolved, or Closed; for custom workflows use the target state's ID. When resolving, supply a resolution.")]
    public Task<string> UpdateTicketStatus(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Target workflow state (label for default workflows, state ID for custom workflows).")] string status,
        [Description("Resolution reason when moving to a resolved state: fixed, cannotResolve, or cancelled.")] string? resolution = null,
        [Description("Optional note recorded with the status change (some transitions require one).")] string? comment = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            string target = ToolValidation.RequireText(status, "status", 128);
            string? res = ToolValidation.OptionalEnum(resolution, "resolution", ToolValidation.Resolutions);
            string? note = ToolValidation.OptionalText(comment, "comment");
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);

            Ticket updated = await _client.UpdateTicketStatusAsync(id, target, res, note, actor.ToTicketUser(), timezoneOffset, cancellationToken);
            return new WriteResult<Ticket>(updated, ActedAs.From(actor));
        });
    }

    private static JsonElement? ValidateCustomFields(Dictionary<string, JsonElement>? customFields)
    {
        if (customFields is null || customFields.Count == 0)
        {
            return null;
        }

        foreach (string key in customFields.Keys)
        {
            if (!Guid.TryParse(key, out _))
            {
                throw new McpException($"'customFields' key '{key}' is not a custom field ID. Use the 36-character IDs from get_instance.");
            }
        }

        return JsonSerializer.SerializeToElement(customFields, TicketingClient.JsonOptions);
    }
}
