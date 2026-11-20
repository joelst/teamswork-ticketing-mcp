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

/// <summary>
/// A queue in one call: someone's tickets (or the team's, or the unassigned ones), ranked, with who each is waiting on.
/// The API can't filter by person, so tickets are read through <see cref="TicketScan"/> and picked here; who a ticket is
/// waiting on comes from its latest history, one request per row, so only the rows returned are read that way, and at
/// most <see cref="HistoryBudget"/> of them.
/// </summary>
[McpServerToolType]
public sealed class QueueTools
{
    private const string QueueFields = "id,ticketId,title,status,priority,assignee,requestor,createdOn,lastUpdatedOn";
    private const int TitleChars = 80;
    private const int LastPublicChars = 160;
    private const int MaxRows = 50;

    /// <summary>History reads one call may make, so a default queue stays well inside a caller's share of the quota.</summary>
    internal const int HistoryBudget = 25;

    private const int HistoryConcurrency = 4;

    private static readonly string[] Roles = ["assignee", "requestor", "either"];
    private static readonly string[] States = ["open", "resolvedSince"];
    private static readonly string[] Sorts = ["urgency", "oldest", "idle", "updated"];
    private static readonly string[] Extras = ["waitingOn", "idleDays", "sla", "lastPublic", "counts"];
    private static readonly string[] DefaultExtras = ["waitingOn", "idleDays", "sla"];

    private readonly TicketingClient _client;
    private readonly IActingUserProvider _actingUser;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;
    private readonly TimeProvider _time;

    public QueueTools(TicketingClient client, IActingUserProvider actingUser, InstanceCache cache, IOptions<TicketingOptions> options, TimeProvider? time = null)
    {
        _client = client;
        _actingUser = actingUser;
        _cache = cache;
        _options = options.Value;
        _time = time ?? TimeProvider.System;
    }

    [McpServerTool(Name = "get_queue", Title = "Get a ticket queue", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "A queue in one call: what's on my plate, someone else's, the team's, or the unassigned tickets, most urgent first, with " +
        "who each is waiting on (waitingOn), days idle and SLA state. Open tickets by default; state=resolvedSince with 'since' " +
        "lists what was resolved, for a standup.")]
    public Task<string> GetQueue(
        [Description("me (default), a person's email or name, team (everyone's tickets), or unassigned.")] string? who = null,
        [Description("assignee (default), requestor, or either: the tickets they're assigned or raised.")] string? role = null,
        [Description("open (default), or resolvedSince with 'since'.")] string? state = null,
        [Description("With state=resolvedSince: resolved on or after this day (YYYY-MM-DD).")] string? since = null,
        [Description("Only this priority: Low, Medium, Important, or Urgent.")] string? priority = null,
        [Description("Per-row extras: waitingOn, idleDays, sla (the default for open tickets; none for resolvedSince), lastPublic (the latest public reply), counts (totals by assignee). waitingOn, idleDays and lastPublic read each row's history: one request per row.")] IReadOnlyList<string>? include = null,
        [Description("urgency (default: SLA breached, Urgent, Important, then waiting on the assignee longest), oldest, idle, or updated.")] string? sort = null,
        [Description("Rows (default 20, max 50).")] int? limit = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string whoText = ToolValidation.OptionalText(who, "who", 320) ?? "me";
            string? givenRole = ToolValidation.OptionalEnum(role, "role", Roles);
            string which = givenRole ?? "assignee";
            bool resolvedSince = (ToolValidation.OptionalEnum(state, "state", States) ?? "open") == "resolvedSince";
            DateOnly? sinceDay = ToolValidation.OptionalDateFilter(since, "since");
            if (resolvedSince != (sinceDay is not null))
            {
                throw new McpException(resolvedSince
                    ? "state=resolvedSince needs 'since', the first day to include (YYYY-MM-DD)."
                    : "'since' goes with state=resolvedSince.");
            }

            ToolValidation.MaxCount(include, "include", Extras.Length);
            // Resolved tickets wait on nobody and have no SLA left to breach, so a resolvedSince list reads no history by default.
            HashSet<string> extras = include is null
                ? new HashSet<string>(resolvedSince ? [] : DefaultExtras, StringComparer.Ordinal)
                : include.Select(x => ToolValidation.RequireEnum(x, "include", Extras)).ToHashSet(StringComparer.Ordinal);
            string order = ToolValidation.OptionalEnum(sort, "sort", Sorts) ?? (resolvedSince ? "updated" : "urgency");
            int max = ToolValidation.ResolvePageSize(limit, Math.Min(_options.DefaultPageSize, MaxRows), MaxRows);

            // Whose queue, and which of their roles. "me" and a person leave out the column that is always them.
            Func<Ticket, bool> matches;
            string label;
            string? whoNote = null;
            bool hideAssignee = false, hideRequestor = false;
            switch (whoText.ToLowerInvariant())
            {
                case "me":
                    ActingUser me = await _actingUser.GetActingUserAsync(cancellationToken);
                    matches = t => HasRole(t, which, u => TicketPeople.IsPerson(u, me.Id, me.Email));
                    label = TicketViews.Person(me.Name, me.Email) ?? me.Id;
                    (hideAssignee, hideRequestor) = (which == "assignee", which == "requestor");
                    whoNote = me.Source == ActingUserSource.DelegatedToken ? null : "No user is signed in, so 'me' is this server's service account.";
                    break;
                case "team":
                    matches = _ => true;
                    label = "team";
                    break;
                case "unassigned":
                    if (givenRole is not (null or "assignee"))
                    {
                        throw new McpException("who=unassigned finds tickets with no assignee, so it takes role 'assignee' only.");
                    }

                    // The same rule count_tickets uses for its "(unassigned)" group, so the two agree.
                    matches = t => Blank(t.Assignee?.Email) is null && Blank(t.Assignee?.Id) is null;
                    label = "unassigned";
                    hideAssignee = true;
                    break;
                default:
                    TicketUser them = await PersonAsync(whoText, timezoneOffset, cancellationToken);
                    matches = t => HasRole(t, which, u => TicketPeople.IsPerson(u, them.Id, them.Email));
                    label = TicketViews.Person(them) ?? them.Email;
                    (hideAssignee, hideRequestor) = (which == "assignee", which == "requestor");
                    break;
            }

            // Two of the SLA flags can't be selected, so asking for SLA state reads whole tickets, unless SLA tracking is off.
            bool? slaOn = extras.Contains("sla") ? await WorkloadTools.SlaEnabledAsync(_cache, _client, timezoneOffset, cancellationToken) : null;
            bool wholeTickets = extras.Contains("sla") && slaOn != false;
            DateTimeOffset? sinceUtc = sinceDay is DateOnly day
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(_client.TimeZones.ResolveOn(timezoneOffset, day)))
                : null;

            var query = new TicketListQuery
            {
                IsResolved = resolvedSince,
                Priority = ToolValidation.OptionalEnum(priority, "priority", ToolValidation.Priorities),
                // A ticket resolved since that day was updated since then; its resolution time narrows it below.
                LastUpdateAfter = sinceDay,
                Select = wholeTickets ? null : resolvedSince ? QueueFields + ",lastResolutionOn" : QueueFields,
                TimezoneOffset = timezoneOffset,
            };

            // Since a day, only tickets whose resolution time shows they were resolved in the period: one updated since (a
            // comment on an old resolved ticket, say) without a resolution time to show it is left out, and counted.
            int undated = 0;
            TicketScan.Result<Ticket> scan = await TicketScan.RunAsync(
                _client,
                query,
                t =>
                {
                    _cache.Numbers.Remember(t);
                    if (!matches(t))
                    {
                        return null;
                    }

                    if (sinceUtc is not null && Waiting.Parse(t.LastResolutionOn) is not DateTimeOffset resolvedOn)
                    {
                        undated++;
                        return null;
                    }

                    return sinceUtc is null || Waiting.Parse(t.LastResolutionOn) >= sinceUtc ? Slim(t) : null;
                },
                _options.MaxScanTickets,
                wholeTickets ? WorkloadTools.FullTicketPageSize : TicketScan.MaxApiPageSize,
                cancellationToken);

            DateTimeOffset now = _time.GetUtcNow();

            // Who each candidate is waiting on, from its history, a few at a time within the budget. Urgency and idle order
            // depend on it, so for those the candidates are more than the rows returned (up to the budget): the rows are
            // picked after it is known. Other orders need only the rows returned.
            bool history = (extras.Contains("waitingOn") && !resolvedSince) || extras.Contains("idleDays") || extras.Contains("lastPublic");
            bool rankedByHistory = history && order is "urgency" or "idle";
            int candidates = rankedByHistory ? Math.Max(max, Math.Min(HistoryBudget, scan.Matches.Count)) : max;
            List<Ticket> page = Coarse(scan.Matches, order, resolvedSince).Take(candidates).ToList();
            var states = new Waiting.State?[page.Count];
            var attempted = new bool[page.Count];
            int read = 0, failed = 0;
            if (history && page.Count > 0)
            {
                TicketUser? caller = null;
                try
                {
                    caller = (await _actingUser.GetActingUserAsync(cancellationToken)).ToTicketUser();
                }
                catch (ActingUserException)
                {
                    // Only who=me needs the caller, and it asked for them above; here they only help tell replies apart.
                }

                TicketPeople people = (await TicketPeople.LoadAsync(_cache, _client, _options, timezoneOffset, cancellationToken))
                    .WithAgents(scan.Matches.Select(t => t.Assignee).Append(caller));
                System.Text.Json.JsonElement? workflows = await WorkflowStatus.InstanceWorkflowsAsync(_cache, _client, timezoneOffset, cancellationToken);
                read = Math.Min(page.Count, HistoryBudget);
                using var gate = new SemaphoreSlim(HistoryConcurrency);
                await Task.WhenAll(Enumerable.Range(0, read).Select(async i =>
                {
                    await gate.WaitAsync(cancellationToken);
                    attempted[i] = true;
                    try
                    {
                        if (Guid.TryParse(page[i].Id, out Guid id))
                        {
                            ListResponse<Activity> h = await _client.ListActivitiesAsync(id, false, Waiting.HistoryWindow, null, cancellationToken);
                            states[i] = Waiting.Compute(page[i], h.Items ?? [], people, resolvedSince, h.ContinuationToken is null, now,
                                WorkflowStatus.LabelOf(page[i], workflows));
                        }
                    }
                    catch (TicketingApiException)
                    {
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            List<(Ticket Ticket, Waiting.State? State, bool Attempted)> rows = Fine(
                    page.Select((t, i) => (t, states[i], attempted[i])).ToList(), order, now)
                .Take(max).ToList();
            int unknown = rows.Count(r => r.State is { WaitingOn: null });
            int unread = history ? rows.Count(r => !r.Attempted) : 0;
            string extrasNamed = string.Join(", ", new[] { "waitingOn", "idleDays", "lastPublic" }.Where(extras.Contains));

            var result = new JsonObject { ["who"] = label };
            if (resolvedSince)
            {
                result["resolvedSince"] = sinceDay!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (extras.Contains("sla") && slaOn == false)
            {
                result["slaTracking"] = "off";
            }

            if (extras.Contains("counts"))
            {
                result["counts"] = Counts(scan.Matches);
            }

            result["items"] = new JsonArray(rows.Select(r => (JsonNode?)Row(r.Ticket, r.State, extras, slaOn != false, hideAssignee, hideRequestor, resolvedSince, now)).ToArray());
            result["returned"] = rows.Count;
            result["matched"] = scan.Matches.Count;

            string?[] hints =
            [
                whoNote,
                scan.Truncated
                    ? $"Only the newest {scan.Scanned} tickets were read (Ticketing:MaxScanTickets), so older ones aren't in this queue; 'priority' narrows what is read."
                    : null,
                undated > 0 ? $"{undated} resolved tickets updated since then have no resolution time to show when they were resolved, so they're left out." : null,
                scan.Matches.Count > rows.Count ? $"{scan.Matches.Count} tickets matched; raise 'limit' (max {MaxRows}) or add filters to see more." : null,
                rankedByHistory && scan.Matches.Count > read
                    ? $"Ranked with the history of {read} of the {scan.Matches.Count} matches, to stay within the API quota: those were picked by SLA, priority and last update, so a ticket outside them waiting on its assignee can rank too low."
                    : null,
                unread > 0
                    ? $"{unread} rows have no {extrasNamed}: only {HistoryBudget} histories are read per call; lower 'limit' to get them for every row."
                    : null,
                // Each input a ranking goes without is said, so a lean 'include' isn't taken for the full order.
                order == "urgency" && !resolvedSince && !wholeTickets && slaOn != false
                    ? "SLA breaches aren't ranked first: include sla (a default) for that."
                    : null,
                order == "urgency" && !resolvedSince && !rankedByHistory
                    ? "Tickets waiting on their assignee aren't ranked ahead of the rest: include waitingOn (a default) for that."
                    : null,
                order == "idle" && !history
                    ? "Ranked by the last update, which automation also moves: include idleDays to rank by the last thing a person did."
                    : null,
                failed > 0 ? $"{failed} rows have no waitingOn: their history couldn't be read (the API quota may be used up for now)." : null,
                unknown > 0 && extras.Contains("waitingOn")
                    ? $"{unknown} rows have no waitingOn: none of their latest {Waiting.HistoryWindow} activities is a public reply, so who they wait on isn't known."
                    : null,
            ];
            if (string.Join(" ", hints.OfType<string>()) is { Length: > 0 } hint)
            {
                result["hint"] = hint;
            }

            return result;
        });
    }

    /// <summary>One queue row: the ticket in brief, and the extras asked for.</summary>
    private static JsonObject Row(Ticket t, Waiting.State? state, HashSet<string> extras, bool slaOn, bool hideAssignee, bool hideRequestor, bool resolvedSince, DateTimeOffset now)
    {
        var o = new JsonObject();
        if (t.TicketNo is int number)
        {
            o["ticketNo"] = number;
        }
        else
        {
            Add(o, "id", t.Id);
        }

        Add(o, "title", TicketViews.Short(t.Title, TitleChars));
        Add(o, "status", t.Status);
        Add(o, "priority", TicketViews.Priority(t.Priority));
        if (!hideAssignee)
        {
            Add(o, "assignee", TicketViews.Person(t.Assignee));
        }

        if (!hideRequestor)
        {
            Add(o, "requestor", TicketViews.Person(t.Requestor));
        }

        if (Waiting.Parse(t.CreatedOn) is DateTimeOffset created)
        {
            o["ageDays"] = Waiting.Days(created, now);
        }

        if (resolvedSince)
        {
            Add(o, "resolvedOn", TicketViews.Time(t.LastResolutionOn));
        }

        if (extras.Contains("idleDays") && state?.IdleDays is int idle)
        {
            o["idleDays"] = idle;
        }

        // When it started waiting is left to get_ticket_brief: idleDays says how stale it is, in far fewer characters.
        if (extras.Contains("waitingOn") && state?.WaitingOn is string waitingOn && !resolvedSince)
        {
            o["waitingOn"] = waitingOn;
        }

        if (extras.Contains("sla") && slaOn)
        {
            Add(o, "sla", TicketViews.Sla(t));
        }

        if (extras.Contains("lastPublic") && state?.LastPublic is Waiting.Reply reply)
        {
            var last = new JsonObject { ["by"] = reply.By };
            if (reply.On is DateTimeOffset on)
            {
                last["on"] = TicketViews.Time(on);
            }

            Add(last, "text", reply.Text is null ? null : TicketViews.Clip(TicketViews.StripQuoted(reply.Text), LastPublicChars));
            o["lastPublic"] = last;
        }

        return o;
    }

    /// <summary>
    /// The order from what every ticket carries, before any history is read: for urgency, SLA breaches, then Urgent, then
    /// Important, then the longest since an update.
    /// </summary>
    private static IEnumerable<Ticket> Coarse(List<Ticket> tickets, string order, bool resolvedSince) => order switch
    {
        "oldest" => tickets.OrderBy(t => Waiting.Parse(t.CreatedOn) ?? DateTimeOffset.MaxValue),
        "idle" => tickets.OrderBy(t => Waiting.Parse(t.LastUpdatedOn) ?? DateTimeOffset.MaxValue),
        // A reopened ticket keeps its old resolution time, so open queues go by the last update; what was resolved since a
        // day goes by when it was resolved.
        "updated" => resolvedSince
            ? tickets.OrderByDescending(t => Waiting.Parse(t.LastResolutionOn) ?? Waiting.Parse(t.LastUpdatedOn) ?? DateTimeOffset.MinValue)
            : tickets.OrderByDescending(t => Waiting.Parse(t.LastUpdatedOn) ?? Waiting.Parse(t.LastResolutionOn) ?? DateTimeOffset.MinValue),
        _ => tickets
            .OrderBy(t => Breached(t) ? 0 : 1)
            .ThenBy(t => PriorityRank(t))
            .ThenBy(t => Waiting.Parse(t.LastUpdatedOn) ?? DateTimeOffset.MaxValue),
    };

    /// <summary>
    /// The returned rows reordered with what their history showed: for urgency, SLA breaches, Urgent and Important first,
    /// then tickets waiting on their assignee, longest waiting first (since the reply they wait after, which a private note
    /// doesn't move), then the rest, idlest first; for idle, by days idle.
    /// </summary>
    private static List<(Ticket Ticket, Waiting.State? State, bool Attempted)> Fine(List<(Ticket Ticket, Waiting.State? State, bool Attempted)> rows, string order, DateTimeOffset now)
    {
        double Idle((Ticket Ticket, Waiting.State? State, bool Attempted) r) =>
            r.State?.IdleDays ?? (Waiting.Parse(r.Ticket.LastUpdatedOn) is DateTimeOffset u ? (now - u).TotalDays : 0);

        return order switch
        {
            "urgency" => rows
                .OrderBy(r => Breached(r.Ticket) ? 0 : PriorityRank(r.Ticket) < 2 ? 1 + PriorityRank(r.Ticket) : r.State?.WaitingOn == Waiting.Assignee ? 3 : 4)
                .ThenBy(r => r.State is { WaitingOn: Waiting.Assignee, WaitingSince: DateTimeOffset since } ? since : DateTimeOffset.MaxValue)
                .ThenByDescending(Idle)
                .ThenBy(r => Waiting.Parse(r.Ticket.CreatedOn) ?? DateTimeOffset.MaxValue)
                .ToList(),
            "idle" => rows.OrderByDescending(Idle).ToList(),
            _ => rows,
        };
    }

    /// <summary>
    /// What a queue keeps of a matched ticket: a scan of whole tickets (for SLA flags) would otherwise hold every match's
    /// description and custom fields until the call ends.
    /// </summary>
    private static Ticket Slim(Ticket t) => new()
    {
        Id = t.Id,
        TicketNo = t.TicketNo,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        Assignee = t.Assignee,
        Requestor = t.Requestor,
        CreatedOn = t.CreatedOn,
        LastUpdatedOn = t.LastUpdatedOn,
        LastResolutionOn = t.LastResolutionOn,
        IsCustomWorkflow = t.IsCustomWorkflow,
        Workflow = t.Workflow,
        IsFrtBreached = t.IsFrtBreached,
        IsRtBreached = t.IsRtBreached,
        IsFrtEscalated = t.IsFrtEscalated,
        IsRtEscalated = t.IsRtEscalated,
    };

    private static bool Breached(Ticket t) => t.IsFrtBreached == true || t.IsRtBreached == true;

    private static int PriorityRank(Ticket t) => TicketViews.Priority(t.Priority) switch
    {
        "Urgent" => 0,
        "Important" => 1,
        _ => 2,
    };

    /// <summary>Totals for the whole match, not just the rows returned: by assignee, largest first, and the unassigned.</summary>
    private static JsonObject Counts(List<Ticket> tickets)
    {
        var byAssignee = new JsonObject();
        foreach (var g in tickets
                     .Where(t => Blank(t.Assignee?.Email) is not null || Blank(t.Assignee?.Id) is not null)
                     .GroupBy(t => Blank(t.Assignee?.Email) ?? Blank(t.Assignee?.Id)!, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
        {
            TicketUser? who = g.First().Assignee;
            string key = Blank(who?.Email) is not null ? TicketViews.Person(who)!
                : Blank(who?.Name) is string name ? $"{name} (ID {g.Key})"
                : g.Key;
            byAssignee[byAssignee.ContainsKey(key) ? $"{key} (ID {g.Key})" : key] = g.Count();
        }

        return new JsonObject
        {
            ["total"] = tickets.Count,
            ["unassigned"] = tickets.Count(t => Blank(t.Assignee?.Email) is null && Blank(t.Assignee?.Id) is null),
            ["byAssignee"] = byAssignee,
        };
    }

    /// <summary>
    /// The person a 'who' names, by the rules every person argument follows: an email or name is looked up in the
    /// assignee list, where a name must match one person. An email that isn't in the list is matched as it is, so a
    /// requestor from outside, or someone since taken off the list, can still be found.
    /// </summary>
    private Task<TicketUser> PersonAsync(string who, int? timezoneOffset, CancellationToken cancellationToken) =>
        FindPersonAsync(_client, _cache, who, "who", timezoneOffset, cancellationToken);

    internal static async Task<TicketUser> FindPersonAsync(TicketingClient client, InstanceCache cache, string who, string paramName, int? timezoneOffset, CancellationToken cancellationToken)
    {
        bool email = who.Contains('@', StringComparison.Ordinal);
        var lookup = new InstanceLookup(client, cache, timezoneOffset, cancellationToken);
        try
        {
            return (await lookup.PersonAsync(email ? new UserRef(Email: who) : new UserRef(Name: who), paramName, assigneeOnly: true))!;
        }
        catch (McpException) when (email)
        {
            string address = ToolValidation.RequireEmail(who, paramName);
            return new TicketUser(address, address, address);
        }
    }

    private static bool HasRole(Ticket t, string role, Func<TicketUser?, bool> isThem) => role switch
    {
        "requestor" => isThem(t.Requestor),
        "either" => isThem(t.Assignee) || isThem(t.Requestor),
        _ => isThem(t.Assignee),
    };

    private static void Add(JsonObject o, string name, string? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
