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
public sealed class ActivityTools
{
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;

    public ActivityTools(TicketingClient client, IActingUserProvider actingUser, InstanceCache cache, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_ticket_activities", Title = "List ticket activities", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "A ticket's activity history (comments, status and field changes), newest first, with quoted email replies removed and " +
        "long text cut. Page back with continuationToken.")]
    public Task<string> ListTicketActivities(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("Maximum activities (default 25, max 100).")] int? limit = null,
        [Description("From the previous page, to get older activity.")] string? continuationToken = null,
        [Description("Also return comment_HTML.")] bool includeHtml = false,
        [Description("Characters of each activity's text to keep (default 600; 20000 for the whole text, quoted email replies included).")] int? maxChars = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            int pageSize = ToolValidation.ResolvePageSize(limit, 25, 100);
            int chars = maxChars is null ? TicketViews.ActivityTextChars : Math.Clamp(maxChars.Value, 100, TicketViews.FullTextChars);
            string? token = ToolValidation.OptionalToken(continuationToken, "continuationToken");
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, null, cancellationToken);

            ListResponse<Activity> r = await _client.ListActivitiesAsync(id, includeHtml, pageSize, token, cancellationToken);
            List<JsonObject> items = (r.Items ?? []).Select(a => TicketViews.Activity(a, includeHtml, chars)).ToList();
            string? hint = r.ContinuationToken is null ? null : "Older activities exist: call again with this continuationToken.";
            return new PageResult<JsonObject>(items, items.Count, null, r.ContinuationToken, hint);
        });
    }

    [McpServerTool(Name = "add_ticket_comment", Title = "Add comment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Add a comment to a ticket as the caller: plain text in 'comment', or formatted in 'commentHtml'. Say whether the requestor " +
        "sees it: isPrivate true for an internal note, false for a reply they're notified of.")]
    public Task<string> AddTicketComment(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("true: an internal note only agents see. false: a public reply the requestor sees and is notified of.")] bool isPrivate,
        [Description("Plain-text comment.")] string? comment = null,
        [Description("HTML comment, used when 'comment' isn't given. Script, forms, images and styles are removed; link a screenshot with add_ticket_link_attachments.")] string? commentHtml = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string? text = ToolValidation.OptionalText(comment, "comment");
            string? html = text is null ? ToolValidation.OptionalHtml(commentHtml, "commentHtml") : null;
            if (text is null && html is null)
            {
                throw new McpException("Provide 'comment' (plain text) or 'commentHtml'.");
            }

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, null, cancellationToken);
            CommentActivity created = await _client.AddCommentAsync(id, text, html, isPrivate, actor.ToTicketUser(), includeHtml: false, cancellationToken);
            _cache.Numbers.Remember(created.TicketNo, id);
            return TicketViews.Ack(created, actor, id, new Dictionary<string, object?> { ["comment"] = isPrivate ? "private note" : "public reply" });
        });
    }
}
