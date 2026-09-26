using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Questions about who has what: the caller's own tickets, SLA risk, and counts. The API can't filter on assignee,
/// requestor, or SLA state, so these tools read tickets through <see cref="TicketScan"/> and filter them here.
/// </summary>
[McpServerToolType]
public sealed class WorkloadTools
{
    private const string SummaryFields = "id,ticketId,title,status,priority,assignee,requestor,expectedDate,createdOn,lastUpdatedOn";

    // Breach flags aren't selectable, so a breach scan reads whole tickets, in smaller pages to keep responses modest.
    private const int FullTicketPageSize = 200;

    private static readonly string[] Roles = ["assignee", "requestor", "either"];
    private static readonly string[] SlaKinds = ["any", "breached", "escalated"];
    private static readonly string[] GroupBys = ["status", "priority", "assignee"];

    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly TicketingOptions _options;

    public WorkloadTools(TicketingClient client, IActingUserProvider actingUser, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _options = options.Value;
    }

    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Show the account this server acts as: the signed-in user, or the configured service account for app-only callers and local " +
        "(stdio) use. Ticket changes are attributed to it, and list_my_tickets matches tickets against it.")]
    public Task<string> WhoAmI(CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            ActingUser actor = await _actingUser.GetActingUserAsync(cancellationToken);
            return new WhoAmIResult(actor.Id, actor.Name, actor.Email, actor.Source.ToString(),
                actor.Source == ActingUserSource.DelegatedToken
                    ? "You are signed in; changes are attributed to you."
                    : "No user is signed in; changes are attributed to this server's configured service account.");
        });
    }

    [McpServerTool(Name = "list_my_tickets", Title = "List my tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "List tickets assigned to or requested by the caller (see whoami), newest first, unresolved only by default. Answers " +
        "'what's on my plate' in one call; the API has no assignee filter, so list_tickets can't. Returns ticket summaries.")]
    public Task<string> ListMyTickets(
        [Description("assignee (default): tickets assigned to me; requestor: tickets I raised; either: both.")] string? role = null,
        [Description("true to include resolved and closed tickets.")] bool includeResolved = false,
        [Description("Only this priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Only tickets created after this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdAfter = null,
        [Description("Maximum tickets to return (default 20, max 100).")] int? limit = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string which = ToolValidation.OptionalEnum(role, "role", Roles) ?? "assignee";
            int max = ToolValidation.ResolvePageSize(limit, _options.DefaultPageSize, _options.MaxPageSize);
            var query = new TicketListQuery
            {
                IsResolved = includeResolved ? null : false,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                CreatedAfter = ToolValidation.OptionalDateTime(createdAfter, "createdAfter"),
                Select = SummaryFields,
                TimezoneOffset = timezoneOffset,
            };
            ActingUser me = await _actingUser.GetActingUserAsync(cancellationToken);

            bool Mine(Ticket t) => which switch
            {
                "requestor" => IsPerson(t.Requestor, me),
                "either" => IsPerson(t.Assignee, me) || IsPerson(t.Requestor, me),
                _ => IsPerson(t.Assignee, me),
            };

            TicketScan.Result<TicketSummary> scan = await TicketScan.RunAsync(
                _client, query, t => Mine(t) ? TicketSummary.From(t) : null, _options.MaxScanTickets, TicketScan.MaxApiPageSize, cancellationToken);
            return Summaries(scan, max);
        });
    }

    [McpServerTool(Name = "list_sla_risk", Title = "List SLA risks", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "List unresolved tickets that have breached or been escalated for their first-response (FRT) or resolution (RT) SLA, newest " +
        "first. Returns ticket summaries with the isFrtBreached, isRtBreached, isFrtEscalated, and isRtEscalated flags.")]
    public Task<string> ListSlaRisk(
        [Description("any (default): breached or escalated; breached: breached only; escalated: escalated only (fastest).")] string? kind = null,
        [Description("Only this priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Only tickets created after this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdAfter = null,
        [Description("Maximum tickets to return (default 20, max 100).")] int? limit = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string which = ToolValidation.OptionalEnum(kind, "kind", SlaKinds) ?? "any";
            int max = ToolValidation.ResolvePageSize(limit, _options.DefaultPageSize, _options.MaxPageSize);
            bool escalatedOnly = which == "escalated";
            var query = new TicketListQuery
            {
                IsResolved = false,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                CreatedAfter = ToolValidation.OptionalDateTime(createdAfter, "createdAfter"),
                // Escalation flags can be selected; breach flags can't, so any other scan needs whole tickets.
                Select = escalatedOnly ? SummaryFields + ",isFrtEscalated,isRtEscalated" : null,
                TimezoneOffset = timezoneOffset,
            };

            bool AtRisk(Ticket t)
            {
                bool breached = t.IsFrtBreached == true || t.IsRtBreached == true;
                bool escalated = t.IsFrtEscalated == true || t.IsRtEscalated == true;
                return which switch
                {
                    "breached" => breached,
                    "escalated" => escalated,
                    _ => breached || escalated,
                };
            }

            int pageSize = escalatedOnly ? TicketScan.MaxApiPageSize : FullTicketPageSize;
            TicketScan.Result<TicketSummary> scan = await TicketScan.RunAsync(
                _client, query, t => AtRisk(t) ? TicketSummary.From(t) : null, _options.MaxScanTickets, pageSize, cancellationToken);
            return Summaries(scan, max);
        });
    }

    [McpServerTool(Name = "count_tickets", Title = "Count tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Count tickets grouped by status, priority, or assignee, for questions like 'how many urgent tickets are open' or 'who has the " +
        "most open tickets'. Unresolved tickets only by default. Returns groups sorted by count, largest first.")]
    public Task<string> CountTickets(
        [Description("Group by: status (default), priority, or assignee.")] string? groupBy = null,
        [Description("true to include resolved and closed tickets.")] bool includeResolved = false,
        [Description("Only this priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Only tickets matching this full-text search.")] string? search = null,
        [Description("Comma-separated tag filter in the form tagCategoryId_tagText (IDs from list_tag_categories).")] string? tags = null,
        [Description("Only tickets created after this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdAfter = null,
        [Description("Only tickets created before this local datetime (YYYY-MM-DD or YYYY-MM-DDTHH:mm:ss).")] string? createdBefore = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string by = ToolValidation.OptionalEnum(groupBy, "groupBy", GroupBys) ?? "status";
            var query = new TicketListQuery
            {
                IsResolved = includeResolved ? null : false,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                Search = ToolValidation.OptionalText(search, "search", 500),
                Tags = ToolValidation.OptionalText(tags, "tags", 2000),
                CreatedAfter = ToolValidation.OptionalDateTime(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateTime(createdBefore, "createdBefore"),
                Select = "id,status,priority,assignee",
                TimezoneOffset = timezoneOffset,
            };

            // Only each ticket's group is kept. A person is grouped by email (or ID when there is none), so one
            // person under two display names is one group, and shown with the first name seen.
            TicketScan.Result<GroupKey> scan = await TicketScan.RunAsync(
                _client, query, t => GroupOf(t, by), _options.MaxScanTickets, TicketScan.MaxApiPageSize, cancellationToken);

            List<CountGroup> groups = scan.Matches
                .GroupBy(k => k.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CountGroup(g.First().Label, g.Count()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CountResult(by, groups, scan.Scanned, scan.Total, scan.Truncated, TicketScan.TruncationHint(scan));
        });
    }

    private static GroupKey GroupOf(Ticket t, string by)
    {
        if (by == "assignee")
        {
            string? identity = Blank(t.Assignee?.Email) ?? Blank(t.Assignee?.Id);
            return identity is null
                ? new GroupKey("", "(unassigned)")
                : new GroupKey(identity, Blank(t.Assignee?.Email) is string email ? $"{Blank(t.Assignee?.Name) ?? email} <{email}>" : Blank(t.Assignee?.Name) ?? identity);
        }

        string? value = Blank(by == "priority" ? t.Priority : t.Status);
        return new GroupKey(value ?? "", value ?? "(none)");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record GroupKey(string Identity, string Label);

    private static bool IsPerson(TicketUser? user, ActingUser me) =>
        user is not null &&
        ((!string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email.Trim(), me.Email.Trim(), StringComparison.OrdinalIgnoreCase)) ||
         (!string.IsNullOrWhiteSpace(user.Id) && string.Equals(user.Id.Trim(), me.Id.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static ScanResult<TicketSummary> Summaries(TicketScan.Result<TicketSummary> scan, int max)
    {
        List<TicketSummary> items = scan.Matches.Take(max).ToList();
        string? more = scan.Matches.Count > max ? $"{scan.Matches.Count} tickets matched; raise 'limit' or add filters to see more." : null;
        string? hint = string.Join(" ", new[] { TicketScan.TruncationHint(scan), more }.OfType<string>()) is { Length: > 0 } both ? both : null;
        return new ScanResult<TicketSummary>(items, items.Count, scan.Matches.Count, scan.Scanned, scan.Total, scan.Truncated, hint);
    }

    private sealed record WhoAmIResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("note")] string Note);

    private sealed record CountGroup(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("count")] int Count);

    private sealed record CountResult(
        [property: JsonPropertyName("groupBy")] string GroupBy,
        [property: JsonPropertyName("groups")] IReadOnlyList<CountGroup> Groups,
        [property: JsonPropertyName("scanned")] int Scanned,
        [property: JsonPropertyName("totalCount")] int? TotalCount,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("hint")] string? Hint);
}
