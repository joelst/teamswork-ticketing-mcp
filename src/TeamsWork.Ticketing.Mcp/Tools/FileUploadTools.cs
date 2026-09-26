using System.ComponentModel;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// File uploads from the local disk. Registered only for the stdio transport, and only when Ticketing:UploadRoot is
/// set: a remote server has no files of the caller's to send, and reading its own disk on a caller's behalf would be
/// a data leak. <see cref="UploadFolder"/> keeps reads inside the configured folder.
/// </summary>
[McpServerToolType]
public sealed class FileUploadTools
{
    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly UploadFolder _folder;

    public FileUploadTools(TicketingClient client, IActingUserProvider actingUser, UploadFolder folder)
    {
        _client = client;
        _actingUser = actingUser;
        _folder = folder;
    }

    [McpServerTool(Name = "upload_ticket_files", Title = "Upload files", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Upload files (for example screenshots or logs) from the local upload folder to a ticket, with an optional comment. Only files " +
        "inside the configured upload folder can be sent; relative paths are taken from it. Up to 10 files per call, within the " +
        "configured total size. The response includes an activityId that list_activity_attachments accepts.")]
    public Task<string> UploadTicketFiles(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Paths of the files to upload, relative to the upload folder (or absolute paths inside it).")] IReadOnlyList<string> paths,
        [Description("Plain-text comment shown with the files.")] string? comment = null,
        [Description("HTML comment (formatting, lists, tables, links). Ignored when 'comment' is also supplied. Script, forms, images, and inline styles are removed.")] string? commentHtml = null,
        [Description("true to make the upload visible only to internal agents.")] bool isPrivate = false,
        [Description("Return comment_HTML in the created activity.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            string? text = ToolValidation.OptionalText(comment, "comment");
            string? html = text is null ? ToolValidation.OptionalHtml(commentHtml, "commentHtml") : null;
            text ??= html is null ? "Files attached." : null;
            IReadOnlyList<UploadFile> files = _folder.ReadFiles(paths);

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            CommentActivity created = await _client.UploadFilesAsync(id, files, text, html, isPrivate, actor.ToTicketUser(), includeHtml, timezoneOffset, cancellationToken);
            return new WriteResult<CommentActivity>(created, ActedAs.From(actor));
        });
    }
}
