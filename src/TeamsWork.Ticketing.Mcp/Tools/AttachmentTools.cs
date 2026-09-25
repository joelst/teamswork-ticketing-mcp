using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

[McpServerToolType]
public sealed class AttachmentTools
{
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;

    public AttachmentTools(TicketingClient client, IActingUserProvider actingUser)
    {
        _client = client;
        _actingUser = actingUser;
    }

    [McpServerTool(Name = "list_ticket_attachments", Title = "List ticket attachments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the files and links attached to a ticket, with captions and download URLs.")]
    public Task<string> ListTicketAttachments(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            ListResponse<Attachment> r = await _client.ListTicketAttachmentsAsync(id, timezoneOffset, cancellationToken);
            IReadOnlyList<Attachment> items = r.Items ?? [];
            return new PageResult<Attachment>(items, items.Count, null, null, null);
        });
    }

    [McpServerTool(Name = "add_ticket_link_attachments", Title = "Attach links", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Attach one or more hyperlinks (for example a SharePoint document or a web page) to a ticket, with an optional comment. " +
        "File uploads are not supported through this tool. The response includes an activityId that list_activity_attachments accepts.")]
    public Task<string> AddTicketLinkAttachments(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Links to attach (at least one).")] IReadOnlyList<LinkRef> links,
        [Description("Plain-text comment shown with the links.")] string? comment = null,
        [Description("Sanitised HTML comment. Ignored when 'comment' is also supplied.")] string? commentHtml = null,
        [Description("true to make the attachment activity visible only to internal agents.")] bool isPrivate = false,
        [Description("Return comment_HTML in the created activity.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            if (links is null || links.Count == 0)
            {
                throw new McpException("'links' must contain at least one {url, caption} entry.");
            }

            if (links.Count > 20)
            {
                throw new McpException("At most 20 links can be attached in one call.");
            }

            var attachmentLinks = links.Select((l, i) => l.ToAttachmentLink(i)).ToList();
            string? text = ToolValidation.OptionalText(comment, "comment");
            string? html = text is null ? ToolValidation.OptionalText(commentHtml, "commentHtml") : null;
            text ??= html is null ? "Links attached." : null;

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            CommentActivity created = await _client.AddLinkAttachmentsAsync(id, attachmentLinks, text, html, isPrivate, actor.ToTicketUser(), includeHtml, timezoneOffset, cancellationToken);
            return new WriteResult<CommentActivity>(created, ActedAs.From(actor));
        });
    }

    [McpServerTool(Name = "list_activity_attachments", Title = "List activity attachments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "List the attachments belonging to one attachment activity. IMPORTANT: 'activityId' must be the activityId returned by " +
        "add_ticket_link_attachments (an attachment activity). IDs from list_ticket_activities for comments or status changes are not " +
        "valid here. To see everything attached to a ticket, use list_ticket_attachments instead.")]
    public Task<string> ListActivityAttachments(
        [Description("ID of an attachment activity (from add_ticket_link_attachments).")] string activityId,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string id = ToolValidation.RequireText(activityId, "activityId", 128);
            ListResponse<Attachment> r = await _client.ListActivityAttachmentsAsync(id, timezoneOffset, cancellationToken);
            IReadOnlyList<Attachment> items = r.Items ?? [];
            return new PageResult<Attachment>(items, items.Count, null, null, null);
        });
    }
}
