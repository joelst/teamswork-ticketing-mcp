using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
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
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;

    public FileUploadTools(TicketingClient client, IActingUserProvider actingUser, UploadFolder folder, InstanceCache cache, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _folder = folder;
        _cache = cache;
        _options = options.Value;
    }

    // OpenWorld: it sends local files to a third-party service, which clients should treat with more care than a
    // change inside the help desk.
    [McpServerTool(Name = "upload_ticket_files", Title = "Upload files", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Upload files (screenshots, logs) from the local upload folder to a ticket, with an optional comment. Paths are relative " +
        "to that folder; up to 10 files within the configured size. Private (agents only) unless isPrivate=false.")]
    public Task<string> UploadTicketFiles(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("Files to upload, relative to the upload folder (or absolute paths inside it).")] IReadOnlyList<string> paths,
        [Description("Plain-text comment shown with the files.")] string? comment = null,
        [Description("HTML comment, used when 'comment' isn't given. Script, forms, images and styles are removed.")] string? commentHtml = null,
        [Description("true (default): only agents see them. false: the requestor sees them too.")] bool isPrivate = true,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string? text = ToolValidation.OptionalText(comment, "comment");
            string? html = text is null ? ToolValidation.OptionalHtml(commentHtml, "commentHtml") : null;
            text ??= html is null ? "Files attached." : null;
            IReadOnlyList<UploadFile> files = _folder.ReadFiles(paths);

            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, timezoneOffset, cancellationToken);
            CommentActivity created = await _client.UploadFilesAsync(id, files, text, html, isPrivate, actor.ToTicketUser(), includeHtml: false, timezoneOffset, cancellationToken);
            _cache.Numbers.Remember(created.TicketNo, id);
            return TicketViews.Ack(created, actor, id, new Dictionary<string, object?>
            {
                ["files"] = files.Select(f => f.FileName).ToList(),
                ["visibility"] = isPrivate ? "private" : "public",
            });
        });
    }
}
