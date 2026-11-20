using System.ComponentModel;
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
public sealed class AttachmentTools
{
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;

    public AttachmentTools(TicketingClient client, IActingUserProvider actingUser, InstanceCache cache, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_ticket_attachments", Title = "List ticket attachments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "List the files and links attached to a ticket. A link's URL is included; a file's download URL (signed, short-lived) " +
        "only with includeUrls.")]
    public Task<string> ListTicketAttachments(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("true to include files' download URLs.")] bool includeUrls = false,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, timezoneOffset, cancellationToken);
            ListResponse<Attachment> r = await _client.ListTicketAttachmentsAsync(id, timezoneOffset, cancellationToken);
            List<JsonObject> items = (r.Items ?? []).Select(a => TicketViews.Attachment(a, includeUrls)).ToList();
            return new PageResult<JsonObject>(items, items.Count, null, null, null);
        });
    }

    [McpServerTool(Name = "add_ticket_link_attachments", Title = "Attach links", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Attach links (a SharePoint document, a web page, a screenshot's location) to a ticket, with an optional comment. Say " +
        "whether the requestor sees them with isPrivate. Files can't be uploaded here.")]
    public Task<string> AddTicketLinkAttachments(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("Links to attach (1 to 20).")] IReadOnlyList<LinkRef> links,
        [Description("true: only agents see them. false: the requestor sees them too.")] bool isPrivate,
        [Description("Plain-text comment shown with the links.")] string? comment = null,
        [Description("HTML comment, used when 'comment' isn't given. Script, forms, images and styles are removed; for a screenshot, put a link to it in 'links'.")] string? commentHtml = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
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
            string? html = text is null ? ToolValidation.OptionalHtml(commentHtml, "commentHtml") : null;
            text ??= html is null ? "Links attached." : null;

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, timezoneOffset, cancellationToken);
            CommentActivity created = await _client.AddLinkAttachmentsAsync(id, attachmentLinks, text, html, isPrivate, actor.ToTicketUser(), includeHtml: false, timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(created.TicketNo, id);
            return TicketViews.Ack(created, actor, id, new Dictionary<string, object?>
            {
                ["links"] = attachmentLinks.Count,
                ["visibility"] = isPrivate ? "private" : "public",
            });
        });
    }
}
