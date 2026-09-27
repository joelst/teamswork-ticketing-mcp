using System.ComponentModel;
using System.Text.Json;
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

    // SLA flags aren't all selectable, so an SLA scan reads whole tickets, in smaller pages to keep responses modest.
    private const int FullTicketPageSize = 200;

    private static readonly string[] Roles = ["assignee", "requestor", "either"];
    private static readonly string[] SlaKinds = ["any", "breached", "escalated"];
    private static readonly string[] GroupBys = ["status", "priority", "assignee"];

    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;

    public WorkloadTools(TicketingClient client, IActingUserProvider actingUser, InstanceCache cache, IOptions<TicketingOptions> options)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
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
        [Description(DateFilterText.CreatedAfter)] string? createdAfter = null,
        [Description(DateFilterText.CreatedBefore)] string? createdBefore = null,
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
                CreatedAfter = ToolValidation.OptionalDateFilter(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateFilter(createdBefore, "createdBefore"),
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
        "first, with the isFrtBreached, isRtBreached, isFrtEscalated, and isRtEscalated flags. If the instance has SLA tracking turned " +
        "off, says so instead of listing nothing.")]
    public Task<string> ListSlaRisk(
        [Description("any (default): breached or escalated; breached: breached only; escalated: escalated only.")] string? kind = null,
        [Description("Only this priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description(DateFilterText.CreatedAfter)] string? createdAfter = null,
        [Description(DateFilterText.CreatedBefore)] string? createdBefore = null,
        [Description("Maximum tickets to return (default 20, max 100).")] int? limit = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string which = ToolValidation.OptionalEnum(kind, "kind", SlaKinds) ?? "any";
            int max = ToolValidation.ResolvePageSize(limit, _options.DefaultPageSize, _options.MaxPageSize);
            var query = new TicketListQuery
            {
                IsResolved = false,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                CreatedAfter = ToolValidation.OptionalDateFilter(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateFilter(createdBefore, "createdBefore"),
                TimezoneOffset = timezoneOffset,
            };

            // With every SLA rule off, the API reports no SLA flags at all, so a scan would find nothing and look like
            // "nothing at risk". Say what is actually true, and save the requests.
            // The settings only save a scan; if they can't be read, scan anyway rather than fail.
            bool? slaEnabled;
            try
            {
                slaEnabled = SlaEnabled((await _cache.GetInstanceAsync(_client, timezoneOffset, refresh: false, cancellationToken)).Sla);
            }
            catch (TicketingApiException)
            {
                slaEnabled = null;
            }

            if (slaEnabled == false)
            {
                return new ScanResult<TicketSummary>([], 0, 0, 0, null, false,
                    "SLA tracking is turned off on this instance (no first-response or resolution rule is enabled), so no ticket can breach " +
                    "or escalate an SLA. A Ticketing administrator can turn it on in the instance's SLA settings.");
            }

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

            // Every mode reads whole tickets, so every result carries all four flags.
            bool anyFlags = false;
            TicketScan.Result<TicketSummary> scan = await TicketScan.RunAsync(
                _client,
                query,
                t =>
                {
                    anyFlags |= t.IsFrtBreached is not null || t.IsRtBreached is not null || t.IsFrtEscalated is not null || t.IsRtEscalated is not null;
                    return AtRisk(t) ? TicketSummary.From(t) : null;
                },
                _options.MaxScanTickets,
                FullTicketPageSize,
                cancellationToken);

            ScanResult<TicketSummary> result = Summaries(scan, max);
            return scan.Scanned > 0 && !anyFlags
                ? result with
                {
                    Hint = "SLA tracking is on, but none of the tickets read carried SLA flags, so this can't tell which are at risk. " +
                           (result.Hint ?? ""),
                }
                : result;
        });
    }

    /// <summary>
    /// Whether any first-response or resolution rule (including escalation) is enabled. True as soon as one is; false
    /// only when both rule groups are present and every rule in them is explicitly disabled; null (so the tool scans
    /// rather than guess "off") when anything is missing or in a shape this server doesn't recognise, since an
    /// unrecognised rule might be an enabled one.
    /// </summary>
    internal static bool? SlaEnabled(JsonElement? sla)
    {
        if (sla is not { ValueKind: JsonValueKind.Object } settings)
        {
            return null;
        }

        bool allUnderstood = true;
        int rulesSeen = 0;
        foreach (string part in (string[])["frt", "rt"])
        {
            if (!settings.TryGetProperty(part, out JsonElement rules) || rules.ValueKind != JsonValueKind.Object)
            {
                allUnderstood = false;
                continue;
            }

            foreach (JsonProperty rule in rules.EnumerateObject())
            {
                JsonValueKind enabled = rule.Value.ValueKind == JsonValueKind.Object && rule.Value.TryGetProperty("enabled", out JsonElement e)
                    ? e.ValueKind
                    : JsonValueKind.Undefined;
                if (enabled == JsonValueKind.True)
                {
                    return true;
                }

                if (enabled == JsonValueKind.False)
                {
                    rulesSeen++;
                }
                else
                {
                    allUnderstood = false;
                }
            }
        }

        return allUnderstood && rulesSeen > 0 ? false : null;
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
        [Description(DateFilterText.CreatedAfter)] string? createdAfter = null,
        [Description(DateFilterText.CreatedBefore)] string? createdBefore = null,
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
                CreatedAfter = ToolValidation.OptionalDateFilter(createdAfter, "createdAfter"),
                CreatedBefore = ToolValidation.OptionalDateFilter(createdBefore, "createdBefore"),
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

    /// <summary>
    /// Whether a ticket's person is the caller. The object ID decides when the ticket has one, so a ticket naming
    /// someone else's ID with the caller's email (or the reverse) isn't counted as theirs; the email decides only for
    /// people recorded without an ID, or in the email-to-ticket form where the ID is the email. A caller who is itself
    /// in the email form (a service account without an object ID) can only be matched by email.
    /// </summary>
    private static bool IsPerson(TicketUser? user, ActingUser me)
    {
        if (user is null)
        {
            return false;
        }

        if (string.Equals(me.Id.Trim(), me.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email.Trim(), me.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        string? id = string.IsNullOrWhiteSpace(user.Id) ? null : user.Id.Trim();
        bool sameEmail = !string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email.Trim(), me.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        if (id is null || string.Equals(id, user.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return sameEmail || string.Equals(id, me.Id.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Compared as object IDs, so one written another way still matches.
        return Guid.TryParse(id, out Guid theirs) && Guid.TryParse(me.Id, out Guid mine) ? theirs == mine : string.Equals(id, me.Id.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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
