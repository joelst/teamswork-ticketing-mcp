using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>Finding tickets the way people refer to them, and reading one ticket's whole story in one call.</summary>
[McpServerToolType]
public sealed class LookupTools
{
    private const string NumberFields = "id,ticketId";
    private const string SimilarFields = "id,ticketId,title,status,priority,assignee,createdOn";

    // Tickets the number search reads before it falls back to paging through tickets sorted by number.
    private const int NumberSearchSize = 50;

    // Every page request costs at least this much of the budget, so pages that come back empty or short still use it up
    // and one lookup makes at most MaxScanTickets / 50 requests (20 by default).
    private const int MinPageCharge = 50;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "but", "are", "was", "has", "have", "can", "cannot", "can't", "cant", "from",
        "this", "that", "our", "your", "you", "all", "any", "when", "what", "why", "how", "does", "doesn't", "into", "onto",
        "please", "help", "need", "issue", "problem", "request", "ticket", "working", "work", "unable", "error",
    };

    private readonly TicketingClient _client;
    private readonly TicketingOptions _options;

    public LookupTools(TicketingClient client, IOptions<TicketingOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    [McpServerTool(Name = "find_ticket_by_number", Title = "Find ticket by number", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get a ticket by its human-readable number (the 'ticketNo' people quote, such as 1234), with the same full details as get_ticket. " +
        "Use this whenever someone refers to a ticket by number; get_ticket and the other tools need the UUID this returns in 'id'.")]
    public Task<string> FindTicketByNumber(
        [Description("Ticket number, such as 1234 (a leading # is fine).")] string ticketNo,
        [Description("Also return description_HTML.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            int number = ParseTicketNumber(ticketNo);
            NumberLookup found = await FindIdByNumberAsync(number, timezoneOffset, cancellationToken);
            return found switch
            {
                { Id: string id } => await _client.GetTicketAsync(Guid.Parse(id), includeHtml, timezoneOffset, cancellationToken),
                { Conclusive: true } => throw new McpException($"There is no ticket number {number}. Check the number, or search with list_tickets."),
                _ => throw new McpException(
                    $"Ticket number {number} wasn't found among the {_options.MaxScanTickets} tickets this lookup may read " +
                    "(Ticketing:MaxScanTickets), so it may still exist. Search for it with list_tickets instead."),
            };
        });
    }

    [McpServerTool(Name = "get_ticket_context", Title = "Get ticket with history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get everything needed to understand a ticket in one call: its full details, its most recent activity (comments, status and field " +
        "changes), and its attachments. Use it before summarising, triaging, or replying to a ticket.")]
    public Task<string> GetTicketContext(
        [Description("Ticket UUID.")] string ticketId,
        [Description("Number of recent activities to include (default 10, max 50).")] int? activityLimit = null,
        [Description("Also return description_HTML and comment_HTML.")] bool includeHtml = false,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
        CancellationToken cancellationToken = default)
    {
        return ToolRunner.RunAsync(async () =>
        {
            Guid id = ToolValidation.RequireGuid(ticketId, "ticketId");
            int activities = ToolValidation.ResolvePageSize(activityLimit, 10, 50);

            // The three reads run together; the first to fail cancels the others (no point spending quota on the history of a
            // ticket that doesn't exist), and its error is the one reported rather than the others' cancellation.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<Ticket> ticket = CancelOthersOnFailure(_client.GetTicketAsync(id, includeHtml, timezoneOffset, linked.Token), linked);
            Task<ListResponse<Activity>> history = CancelOthersOnFailure(_client.ListActivitiesAsync(id, includeHtml, activities, null, linked.Token), linked);
            Task<ListResponse<Attachment>> attachments = CancelOthersOnFailure(_client.ListTicketAttachmentsAsync(id, timezoneOffset, linked.Token), linked);
            try
            {
                await Task.WhenAll(ticket, history, attachments);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                Task[] all = [ticket, history, attachments];
                if (all.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException is Exception first)
                {
                    ExceptionDispatchInfo.Throw(first);
                }

                throw;
            }

            ListResponse<Activity> h = await history;
            return new TicketContext(
                await ticket,
                h.Items ?? [],
                h.ContinuationToken,
                h.ContinuationToken is null ? null : "Older activity exists: call list_ticket_activities with this continuationToken.",
                (await attachments).Items ?? []);
        });
    }

    [McpServerTool(Name = "find_similar_tickets", Title = "Find similar tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find existing tickets whose titles resemble a proposed title, to avoid filing a duplicate. Call it before create_ticket and, if a " +
        "match looks like the same problem, comment on that ticket instead. Searches unresolved tickets by default; results are ranked " +
        "by how many of the title's keywords they share (score 0 to 1).")]
    public Task<string> FindSimilarTickets(
        [Description("Proposed ticket title, or a short description of the problem.")] string title,
        [Description("true to include resolved and closed tickets.")] bool includeResolved = false,
        [Description("Maximum matches to return (default 5, max 20).")] int? limit = null,
        [Description("Caller's UTC offset in whole hours. Defaults to the server's configured time zone.")] int? timezoneOffset = null,
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
                List<SimilarTicket> unranked = found.Values.Take(max).Select(t => new SimilarTicket(null, TicketSummary.From(t))).ToList();
                return new SimilarResult(unranked, keywords, unranked.Count == 0
                    ? "No similar tickets found; it's reasonable to create a new one."
                    : "The title has no words to compare, so these search results are unranked. Check them before creating a ticket.");
            }

            List<SimilarTicket> ranked = found.Values
                .Select(t => new SimilarTicket(Math.Round(Similarity(keywords, t.Title), 2), TicketSummary.From(t)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .Take(max)
                .ToList();

            return new SimilarResult(ranked, keywords,
                ranked.Count == 0 ? "No similar tickets found; it's reasonable to create a new one." : null);
        });
    }

    /// <summary>
    /// Finds a ticket's UUID from its number. The API can't filter by number, so this tries full-text search (which
    /// matches most numbers) and then pages through tickets sorted by number, newest first. Each request is charged the
    /// rows it returns, and at least <see cref="MinPageCharge"/>, against Ticketing:MaxScanTickets, so an empty page isn't
    /// free and the number of requests is bounded. A number is reported absent only on the evidence of one response: above the newest number, numbers on
    /// both sides of it on one contiguous page, or the list reported to end before it. Tickets created or deleted
    /// between requests shift positions, so evidence is never combined across responses.
    /// </summary>
    private async Task<NumberLookup> FindIdByNumberAsync(int number, int? timezoneOffset, CancellationToken cancellationToken)
    {
        int budget = _options.MaxScanTickets;

        int searchSize = Math.Min(NumberSearchSize, budget);
        ListResponse<Ticket> searched = await ListByNumberAsync(null, searchSize, search: number, timezoneOffset, cancellationToken);
        budget -= searchSize;
        if (Match(searched.Items, number) is NumberLookup found)
        {
            return found;
        }

        if (budget <= 0)
        {
            return new NumberLookup(null, false);
        }

        ListResponse<Ticket> newest = await ListByNumberAsync(null, 1, search: null, timezoneOffset, cancellationToken);
        budget--;
        if (newest.Items is { Count: 0 })
        {
            return new NumberLookup(null, true); // no tickets at all
        }

        if (newest.Items is not [Ticket top] || TicketSummary.TicketNumber(top) is not int highest)
        {
            return new NumberLookup(null, false);
        }

        if (number > highest)
        {
            return new NumberLookup(null, true);
        }

        if (number == highest)
        {
            return Match(newest.Items, number)!;
        }

        // Tickets numbered above this one come before it, so it sits at position (highest - number) or earlier: deleted
        // or skipped numbers only move it earlier. It can't be past the last position either, which matters when
        // numbering doesn't start at 1. The first page ends at that position, sized by what is left of the budget.
        int page = Math.Min(TicketScan.MaxApiPageSize, _options.MaxScanTickets);
        int likeliest = highest - number;
        if (newest.ItemCount is int count && count > 0)
        {
            likeliest = Math.Min(likeliest, count - 1);
        }

        int offset = Math.Max(0, likeliest - Math.Min(page, budget) + 1);
        var visited = new HashSet<int>();
        while (budget > 0 && visited.Add(offset))
        {
            int size = Math.Min(page, budget);
            ListResponse<Ticket> window = await ListByNumberAsync(offset, size, search: null, timezoneOffset, cancellationToken);
            IReadOnlyList<Ticket> items = window.Items ?? [];
            budget -= Math.Max(items.Count, MinPageCharge);

            if (Match(items, number) is NumberLookup hit)
            {
                return hit;
            }

            // A row without its number could be the ticket, so a page with one proves nothing about absence.
            List<int> numbers = items.Select(TicketSummary.TicketNumber).OfType<int>().ToList();
            bool complete = numbers.Count == items.Count && items.Count > 0;
            bool atEnd = window.ItemCount is int total && offset + items.Count >= total;

            if (items.Count == 0)
            {
                if (offset == 0)
                {
                    return new NumberLookup(null, false);
                }

                offset = Math.Max(0, offset - size); // past the end, as the list shrank: step back
                continue;
            }

            if (numbers.Count > 0 && numbers.Max() < number)
            {
                if (offset == 0)
                {
                    // This page starts at the newest ticket and every number on it is below this one.
                    return new NumberLookup(null, complete);
                }

                // It is earlier. Step back so the next page overlaps this one by a row: if this page's first number and
                // the previous one straddle it, the next page shows both, and one response settles it.
                offset = Math.Max(0, offset - Math.Max(1, size - 1));
            }
            else if (numbers.Count > 0 && numbers.Min() > number)
            {
                if (complete && atEnd)
                {
                    return new NumberLookup(null, true); // the list ends here, still above it
                }

                if (atEnd)
                {
                    return new NumberLookup(null, false);
                }

                // It is later. Step forward overlapping by a row, for the same reason.
                offset += Math.Max(1, items.Count - 1);
            }
            else
            {
                // Numbers on both sides of it on one contiguous page, and it isn't there.
                return new NumberLookup(null, complete);
            }
        }

        return new NumberLookup(null, false);
    }

    /// <summary>
    /// The lookup result if a row carries the number: its UUID, or an error when the API gave that row no usable ID
    /// (reporting such a ticket as missing would be wrong).
    /// </summary>
    private static NumberLookup? Match(IReadOnlyList<Ticket>? tickets, int number)
    {
        Ticket? row = tickets?.FirstOrDefault(t => TicketSummary.TicketNumber(t) == number);
        if (row is null)
        {
            return null;
        }

        return Guid.TryParse(row.Id, out _)
            ? new NumberLookup(row.Id, true)
            : throw new McpException($"Ticket number {number} exists, but the Ticketing API returned no usable ID for it. Try list_tickets with a search for it.");
    }

    private Task<ListResponse<Ticket>> ListByNumberAsync(int? offset, int limit, int? search, int? timezoneOffset, CancellationToken cancellationToken) =>
        _client.ListTicketsAsync(new TicketListQuery
        {
            Search = search?.ToString(CultureInfo.InvariantCulture),
            OrderBy = search is null ? "ticketId" : null,
            Order = search is null ? "DESC" : null,
            Select = NumberFields,
            Offset = offset is > 0 ? offset : null,
            Limit = limit,
            TimezoneOffset = timezoneOffset,
        }, cancellationToken);

    /// <summary>The ticket's UUID when found; otherwise whether its absence is certain.</summary>
    private sealed record NumberLookup(string? Id, bool Conclusive);

    internal static int ParseTicketNumber(string? value)
    {
        string text = ToolValidation.RequireText(value, "ticketNo", 20).TrimStart('#').Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw new McpException("'ticketNo' must be a ticket number such as 1234. For a UUID, use get_ticket.");
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

    private sealed record TicketContext(
        [property: JsonPropertyName("ticket")] Ticket Ticket,
        [property: JsonPropertyName("activities")] IReadOnlyList<Activity> Activities,
        [property: JsonPropertyName("activitiesContinuationToken")] string? ActivitiesContinuationToken,
        [property: JsonPropertyName("hint")] string? Hint,
        [property: JsonPropertyName("attachments")] IReadOnlyList<Attachment> Attachments);

    private sealed record SimilarTicket(
        [property: JsonPropertyName("score")] double? Score,
        [property: JsonPropertyName("ticket")] TicketSummary Ticket);

    private sealed record SimilarResult(
        [property: JsonPropertyName("matches")] IReadOnlyList<SimilarTicket> Matches,
        [property: JsonPropertyName("keywords")] IReadOnlyList<string> Keywords,
        [property: JsonPropertyName("hint")] string? Hint);
}
