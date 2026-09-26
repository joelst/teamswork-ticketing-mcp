using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>Finding tickets the way people refer to them, and reading one ticket's whole story in one call.</summary>
[McpServerToolType]
public sealed class LookupTools
{
    private const string NumberFields = "id,ticketId";
    private const string SimilarFields = "id,ticketId,title,status,priority,assignee,createdOn";

    // How far below its estimated position a ticket number may sit (deleted tickets shift it) and still be found.
    private const int NumberWindow = TicketScan.MaxApiPageSize;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "but", "are", "was", "has", "have", "can", "cannot", "can't", "cant", "from",
        "this", "that", "our", "your", "you", "all", "any", "when", "what", "why", "how", "does", "doesn't", "into", "onto",
        "please", "help", "need", "issue", "problem", "request", "ticket", "working", "work", "unable", "error",
    };

    private readonly TicketingClient _client;

    public LookupTools(TicketingClient client)
    {
        _client = client;
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
            string? id = await FindIdByNumberAsync(number, timezoneOffset, cancellationToken)
                         ?? throw new McpException($"No ticket number {number} was found. Check the number, or search with list_tickets.");
            return await _client.GetTicketAsync(Guid.Parse(id), includeHtml, timezoneOffset, cancellationToken);
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

            Task<Ticket> ticket = _client.GetTicketAsync(id, includeHtml, timezoneOffset, cancellationToken);
            Task<ListResponse<Activity>> history = _client.ListActivitiesAsync(id, includeHtml, activities, null, cancellationToken);
            Task<ListResponse<Attachment>> attachments = _client.ListTicketAttachmentsAsync(id, timezoneOffset, cancellationToken);
            await Task.WhenAll(ticket, history, attachments);

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
            List<string> keywords = Keywords(text);

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
    /// Finds a ticket's UUID from its number. The API can't filter by number, so this first tries full-text search
    /// (which may match the number), then reads the page of tickets, sorted by number, where the number should be.
    /// </summary>
    private async Task<string?> FindIdByNumberAsync(int number, int? timezoneOffset, CancellationToken cancellationToken)
    {
        ListResponse<Ticket> searched = await _client.ListTicketsAsync(new TicketListQuery
        {
            Search = number.ToString(CultureInfo.InvariantCulture),
            Select = NumberFields,
            Limit = 50,
            TimezoneOffset = timezoneOffset,
        }, cancellationToken);

        string? id = IdOf(searched.Items, number);
        if (id is not null)
        {
            return id;
        }

        // Newest number first. Tickets numbered above this one come before it, so its position is at most
        // (highest - number); deleted tickets only move it earlier. One page ending at that position covers it
        // unless more than a page's worth of tickets above it were deleted.
        ListResponse<Ticket> newest = await _client.ListTicketsAsync(new TicketListQuery
        {
            OrderBy = "ticketId",
            Order = "DESC",
            Select = NumberFields,
            Limit = 1,
            TimezoneOffset = timezoneOffset,
        }, cancellationToken);

        if (newest.Items is not [Ticket top] || TicketSummary.TicketNumber(top) is not int highest || number > highest)
        {
            return null;
        }

        if (highest == number)
        {
            return top.Id;
        }

        int position = highest - number;
        ListResponse<Ticket> window = await _client.ListTicketsAsync(new TicketListQuery
        {
            OrderBy = "ticketId",
            Order = "DESC",
            Select = NumberFields,
            Offset = position >= NumberWindow ? position - NumberWindow + 1 : null,
            Limit = Math.Min(NumberWindow, position + 1),
            TimezoneOffset = timezoneOffset,
        }, cancellationToken);

        return IdOf(window.Items, number);
    }

    private static string? IdOf(IReadOnlyList<Ticket>? tickets, int number) =>
        tickets?.FirstOrDefault(t => TicketSummary.TicketNumber(t) == number && Guid.TryParse(t.Id, out _))?.Id;

    internal static int ParseTicketNumber(string? value)
    {
        string text = ToolValidation.RequireText(value, "ticketNo", 20).TrimStart('#').Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw new McpException("'ticketNo' must be a ticket number such as 1234. For a UUID, use get_ticket.");
    }

    /// <summary>Distinctive lower-case words of three or more letters, without common filler words.</summary>
    internal static List<string> Keywords(string? text) =>
        (text ?? "")
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'').ToLowerInvariant())
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

    /// <summary>Share of the query's keywords that also appear in the candidate's title.</summary>
    internal static double Similarity(List<string> queryKeywords, string? candidateTitle)
    {
        if (queryKeywords.Count == 0)
        {
            return 0;
        }

        var candidate = new HashSet<string>(Keywords(candidateTitle), StringComparer.Ordinal);
        return (double)queryKeywords.Count(candidate.Contains) / queryKeywords.Count;
    }

    private static readonly char[] WordSeparators = " \t\r\n.,;:!?()[]{}<>\"/\\|-_+=*&^%$#@~`".ToCharArray();

    private sealed record TicketContext(
        [property: JsonPropertyName("ticket")] Ticket Ticket,
        [property: JsonPropertyName("activities")] IReadOnlyList<Activity> Activities,
        [property: JsonPropertyName("activitiesContinuationToken")] string? ActivitiesContinuationToken,
        [property: JsonPropertyName("hint")] string? Hint,
        [property: JsonPropertyName("attachments")] IReadOnlyList<Attachment> Attachments);

    private sealed record SimilarTicket(
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("ticket")] TicketSummary Ticket);

    private sealed record SimilarResult(
        [property: JsonPropertyName("matches")] IReadOnlyList<SimilarTicket> Matches,
        [property: JsonPropertyName("keywords")] IReadOnlyList<string> Keywords,
        [property: JsonPropertyName("hint")] string? Hint);
}
