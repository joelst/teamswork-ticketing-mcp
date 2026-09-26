using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

[McpServerToolType]
public sealed class ActivityTools
{
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;

    public ActivityTools(TicketingClient client, IActingUserProvider actingUser)
    {
        _client = client;
        _actingUser = actingUser;
    }

    [McpServerTool(Name = "list_ticket_activities", Title = "List ticket activities", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get the activity history of a ticket (comments, status changes, field changes, attachment events), newest first. " +
        "Use 'limit' to keep the response small and the returned continuationToken to page further back.")]
    public Task<string> ListTicketActivities(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Maximum number of activities to return (default 25, max 100).")] int? limit = null,
        [Description("Token from a previous list_ticket_activities response to fetch the next page.")] string? continuationToken = null,
        [Description("Also return comment_HTML (sanitised HTML) alongside the plain-text comment.")] bool includeHtml = false,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            int pageSize = ToolValidation.ResolvePageSize(limit, 25, 100);
            string? token = ToolValidation.OptionalToken(continuationToken, "continuationToken");

            ListResponse<Activity> r = await _client.ListActivitiesAsync(id, includeHtml, pageSize, token, cancellationToken);
            IReadOnlyList<Activity> items = r.Items ?? [];
            string? hint = r.ContinuationToken is null ? null : "More activities are available: call again with this continuationToken.";
            return new PageResult<Activity>(items, items.Count, null, r.ContinuationToken, hint);
        });
    }

    [McpServerTool(Name = "add_ticket_comment", Title = "Add comment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Add a comment to a ticket as the signed-in user. Pass plain text in 'comment' or formatted content in 'commentHtml'. " +
        "Set isPrivate=true for internal-only notes that the requestor should not see.")]
    public Task<string> AddTicketComment(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Plain-text comment. HTML characters are escaped.")] string? comment = null,
        [Description("HTML comment (formatting, lists, tables, links). Ignored when 'comment' is also supplied. Script, forms, images, and inline styles are removed; use add_ticket_link_attachments for screenshots.")] string? commentHtml = null,
        [Description("true to make the comment visible only to internal agents.")] bool isPrivate = false,
        [Description("Return comment_HTML in the created activity.")] bool includeHtml = false,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            string? text = ToolValidation.OptionalText(comment, "comment");
            string? html = text is null ? ToolValidation.OptionalHtml(commentHtml, "commentHtml") : null;
            if (text is null && html is null)
            {
                throw new McpException("Provide 'comment' (plain text) or 'commentHtml'.");
            }

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            CommentActivity created = await _client.AddCommentAsync(id, text, html, isPrivate, actor.ToTicketUser(), includeHtml, cancellationToken);
            return new WriteResult<CommentActivity>(created, ActedAs.From(actor));
        });
    }
}
