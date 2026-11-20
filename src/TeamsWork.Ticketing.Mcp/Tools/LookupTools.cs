using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>Reading one ticket's whole story in one call, and finding tickets like a proposed one.</summary>
[McpServerToolType]
public sealed class LookupTools
{
    // Each activity's text in a brief: five long notes at this length keep a brief near 4 KB; maxChars asks for more.
    private const int BriefTextChars = 400;

    private const string SimilarFields = "id,ticketId,title,status,priority,assignee,createdOn";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "but", "are", "was", "has", "have", "can", "cannot", "can't", "cant", "from",
        "this", "that", "our", "your", "you", "all", "any", "when", "what", "why", "how", "does", "doesn't", "into", "onto",
        "please", "help", "need", "issue", "problem", "request", "ticket", "working", "work", "unable", "error",
    };

    private readonly TicketingClient _client;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;
    private readonly TimeProvider _time;
    private readonly IActingUserProvider? _actingUser;

    public LookupTools(TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options, TimeProvider? time = null, IActingUserProvider? actingUser = null)
    {
        _client = client;
        _cache = cache;
        _options = options.Value;
        _actingUser = actingUser;
        _time = time ?? TimeProvider.System;
    }

    [McpServerTool(Name = "get_ticket_brief", Title = "Get ticket brief", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything needed to act on one ticket, in one call: its details, allowedNextStatuses, who it is waiting on (waitingOn, " +
        "idleDays), its latest activity (quoted email replies removed, long text cut) and attachments. activities=0 returns the " +
        "details alone, in one request.")]
    public Task<string> GetTicketBrief(
        [Description(TicketRefs.ParamText)] string ticket,
        [Description("Latest activities to include (default 5, max 50; 0 for the details alone).")] int? activities = null,
        [Description("Characters of text to keep in each activity and the description (defaults 400 and 1000; 20000 for the whole text, quoted email replies included).")] int? maxChars = null,
        [Description("Include private notes (default true).")] bool includePrivate = true,
        [Description("Include what automation rules did (default false).")] bool includeAutomation = false,
        [Description("Also return description_HTML and comment_HTML.")] bool includeHtml = false,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            int shown = activities is null ? 5 : activities < 0 ? throw new McpException("'activities' must be 0 or more.") : Math.Min(activities.Value, 50);
            int chars = maxChars is null ? BriefTextChars : Math.Clamp(maxChars.Value, 100, TicketViews.FullTextChars);
            int detailChars = maxChars is null ? TicketViews.DetailTextChars : chars;
            Guid id = await new TicketRefs(_client, _cache, _options).ResolveAsync(ticket, timezoneOffset, cancellationToken);

            if (shown == 0)
            {
                Ticket alone = await _client.GetTicketAsync(id, includeHtml, timezoneOffset, cancellationToken);
                _cache.Numbers.Remember(alone);
                return TicketViews.Detail(alone, includeHtml, await WorkflowStatus.WorkflowsForAsync(alone, _cache, _client, timezoneOffset, cancellationToken), detailChars);
            }

            // The three reads run together; the first to fail cancels the others (no point spending quota on the history of a
            // ticket that doesn't exist), and its error is the one reported rather than the others' cancellation. Enough
            // history is read to find the latest reply, whatever is shown: it is one request either way.
            int read = Math.Max(shown, Waiting.HistoryWindow);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<Ticket> details = CancelOthersOnFailure(_client.GetTicketAsync(id, includeHtml, timezoneOffset, linked.Token), linked);
            Task<ListResponse<Activity>> history = CancelOthersOnFailure(_client.ListActivitiesAsync(id, includeHtml, read, null, linked.Token), linked);
            Task<ListResponse<Attachment>> attachments = CancelOthersOnFailure(_client.ListTicketAttachmentsAsync(id, timezoneOffset, linked.Token), linked);
            try
            {
                await Task.WhenAll(details, history, attachments);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                Task[] all = [details, history, attachments];
                if (all.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException is Exception first)
                {
                    ExceptionDispatchInfo.Throw(first);
                }

                throw;
            }

            Ticket t = await details;
            _cache.Numbers.Remember(t);
            ListResponse<Activity> h = await history;
            IReadOnlyList<Activity> recent = h.Items ?? [];
            System.Text.Json.JsonElement? workflows = await WorkflowStatus.WorkflowsForAsync(t, _cache, _client, timezoneOffset, cancellationToken);
            JsonObject brief = TicketViews.Detail(t, includeHtml, workflows, detailChars);

            // The caller counts as staff, as in get_queue, so their own replies aren't taken for the requestor's.
            TicketPeople people = await TicketPeople.LoadAsync(_cache, _client, _options, timezoneOffset, cancellationToken);
            if (await CallerAsync(cancellationToken) is TicketUser caller)
            {
                people.WithAgents([caller]);
            }

            DateTimeOffset now = _time.GetUtcNow();
            bool? resolved = brief["isResolved"]?.GetValue<bool>();
            Waiting.State state = Waiting.Compute(t, recent, people, resolved, complete: h.ContinuationToken is null, now,
                WorkflowStatus.LabelOf(t, workflows ?? await WorkflowStatus.InstanceWorkflowsAsync(_cache, _client, timezoneOffset, cancellationToken)));
            if (Waiting.Parse(t.CreatedOn) is DateTimeOffset created)
            {
                brief["ageDays"] = Waiting.Days(created, now);
            }

            if (state.IdleDays is int idle)
            {
                brief["idleDays"] = idle;
            }

            if (state.WaitingOn is string waitingOn)
            {
                brief["waitingOn"] = waitingOn;
            }
            if (state.WaitingSince is DateTimeOffset since)
            {
                brief["waitingSince"] = TicketViews.Time(since);
            }

            bool Hidden(Activity a) => (!includeAutomation && a.IsAutomation) || (!includePrivate && a.IsPrivate == true);
            List<Activity> kept = recent.Where(a => !Hidden(a)).ToList();
            brief["activities"] = new JsonArray(kept.Take(shown).Select(a => (JsonNode?)TicketViews.Activity(a, includeHtml, chars)).ToArray());

            // What was left out from among the activities shown: older ones are covered by the "older activity" note.
            int automation = 0, notes = 0, listed = 0;
            foreach (Activity a in recent.TakeWhile(_ => listed < shown))
            {
                if (!Hidden(a))
                {
                    listed++;
                }
                else if (!includeAutomation && a.IsAutomation)
                {
                    automation++;
                }
                else
                {
                    notes++;
                }
            }

            string? more = kept.Count > shown || h.ContinuationToken is not null
                ? "Older activity: raise 'activities' (up to 50) or call list_ticket_activities."
                : null;
            string?[] left =
            [
                automation > 0 ? $"{automation} by automation" : null,
                notes > 0 ? $"{notes} private {(notes == 1 ? "note" : "notes")}" : null,
            ];
            string? leftOut = left.Any(x => x is not null) ? $"Left out: {string.Join(" and ", left.OfType<string>())} (includeAutomation, includePrivate)." : null;
            string? unknown = state.WaitingOn is null
                ? $"waitingOn is left out: none of the latest {recent.Count} activities is a public reply; list_ticket_activities has older ones."
                : null;
            if (string.Join(" ", new[] { unknown, leftOut, more }.OfType<string>()) is { Length: > 0 } hint)
            {
                brief["hint"] = hint;
            }

            if ((await attachments).Items is { Count: > 0 } files)
            {
                brief["attachments"] = new JsonArray(files.Select(a => (JsonNode?)TicketViews.Attachment(a, includeUrl: false)).ToArray());
            }

            return brief;
        });
    }

    [McpServerTool(Name = "find_similar_tickets", Title = "Find similar tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find tickets whose titles resemble a proposed one, ranked by shared keywords (score 0 to 1), to avoid a duplicate: call it " +
        "before create_ticket, and comment on a match instead if it's the same problem. Unresolved tickets by default.")]
    public Task<string> FindSimilarTickets(
        [Description("Proposed title, or a short description of the problem.")] string title,
        [Description("true to include resolved and closed tickets.")] bool includeResolved = false,
        [Description("Maximum matches (default 5, max 20).")] int? limit = null,
        [Description(DateFilterText.TimezoneOffset)] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            string text = ToolValidation.RequireText(title, "title", 500);
            int max = ToolValidation.ResolvePageSize(limit, 5, 20);

            // A title made only of short or common words ("PC", "VPN issue help") has no distinctive keywords; its plain
            // words are compared instead, so a real duplicate isn't scored 0 and hidden.
            List<string> keywords = Keywords(text, MaxQueryKeywords);
            if (keywords.Count == 0)
            {
                keywords = Words(text).Take(MaxQueryKeywords).ToList();
            }

            // The whole phrase first, then the most distinctive words on their own, since the API's full-text search
            // may require every word to match. At most four requests.
            var searches = new List<string> { text };
            searches.AddRange(keywords.OrderByDescending(k => k.Length).Take(3));

            var found = new Dictionary<string, Ticket>(StringComparer.OrdinalIgnoreCase);
            foreach (string search in searches.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ListResponse<Ticket> r = await _client.ListTicketsAsync(new TicketListQuery
                {
                    Search = search,
                    IsResolved = includeResolved ? null : false,
                    Select = SimilarFields,
                    Limit = 25,
                    TimezoneOffset = timezoneOffset,
                }, cancellationToken);
                _cache.Numbers.Remember(r.Items);

                foreach (Ticket t in r.Items ?? [])
                {
                    if (t.Id is not null)
                    {
                        found.TryAdd(t.Id, t);
                    }
                }
            }

            if (keywords.Count == 0)
            {
                // Nothing to score with: return what the phrase search found, unranked, rather than claiming there's none.
                List<JsonObject> unranked = found.Values.Take(max).Select(t => Scored(null, t)).ToList();
                return new SimilarResult(unranked, keywords, unranked.Count == 0
                    ? "No similar tickets found; it's reasonable to create a new one."
                    : "The title has no words to compare, so these search results are unranked. Check them before creating a ticket.");
            }

            List<JsonObject> ranked = found.Values
                .Select(t => (Score: Math.Round(Similarity(keywords, t.Title), 2), Ticket: t))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .Take(max)
                .Select(m => Scored(m.Score, m.Ticket))
                .ToList();

            return new SimilarResult(ranked, keywords,
                ranked.Count == 0 ? "No similar tickets found; it's reasonable to create a new one." : null);
        });
    }

    /// <summary>A match: its score first, then the ticket's row.</summary>
    private static JsonObject Scored(double? score, Ticket t)
    {
        var o = new JsonObject();
        if (score is double s)
        {
            o["score"] = s;
        }

        foreach ((string key, JsonNode? value) in TicketViews.Row(t).ToList())
        {
            o[key] = value?.DeepClone();
        }

        return o;
    }

    // Keywords taken from the proposed title; a candidate's title is compared in full.
    private const int MaxQueryKeywords = 12;

    /// <summary>Distinctive lower-case words of three or more letters, without common filler words.</summary>
    internal static List<string> Keywords(string? text, int max = int.MaxValue) =>
        Words(text).Where(w => w.Length >= 3 && !StopWords.Contains(w)).Take(max).ToList();

    /// <summary>The distinct lower-case words of <paramref name="text"/>.</summary>
    private static IEnumerable<string> Words(string? text) =>
        (text ?? "")
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'').ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal);

    /// <summary>Share of the query's words that also appear in the candidate's title.</summary>
    internal static double Similarity(List<string> queryWords, string? candidateTitle)
    {
        if (queryWords.Count == 0)
        {
            return 0;
        }

        var candidate = new HashSet<string>(Words(candidateTitle), StringComparer.Ordinal);
        return (double)queryWords.Count(candidate.Contains) / queryWords.Count;
    }

    /// <summary>The caller, for telling their replies apart; null when there is none to tell (a read needs no caller).</summary>
    private async Task<TicketUser?> CallerAsync(CancellationToken cancellationToken)
    {
        if (_actingUser is null)
        {
            return null;
        }

        try
        {
            return (await _actingUser.GetActingUserAsync(cancellationToken)).ToTicketUser();
        }
        catch (ActingUserException)
        {
            return null;
        }
    }

    private static async Task<T> CancelOthersOnFailure<T>(Task<T> task, CancellationTokenSource others)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await others.CancelAsync();
            throw;
        }
    }

    private static readonly char[] WordSeparators = " \t\r\n.,;:!?()[]{}<>\"/\\|-_+=*&^%$#@~`".ToCharArray();

    private sealed record SimilarResult(
        [property: JsonPropertyName("matches")] IReadOnlyList<JsonObject> Matches,
        [property: JsonPropertyName("keywords")] IReadOnlyList<string> Keywords,
        [property: JsonPropertyName("hint")] string? Hint);
}
