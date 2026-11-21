using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Turns the ticket references people use (2213, #2213, ITH-2213) or a UUID into the UUID the API takes, so an agent
/// never needs a lookup call first. Numbers seen in earlier responses are answered from <see cref="TicketNumbers"/>;
/// the rest are found as find-by-number always did.
/// </summary>
internal sealed partial class TicketRefs
{
    public const string ParamText = "The ticket: its number (2213 or #2213) or its UUID.";

    // Tickets the number search reads before it falls back to paging through tickets sorted by number.
    private const int NumberSearchSize = 50;

    // Every page request costs at least this much of the budget, so pages that come back empty or short still use it up, and
    // one lookup makes at most MaxScanTickets / 50 + 1 requests (21 by default: the search, the newest-ticket read, pages).
    private const int MinPageCharge = 50;

    private const string NumberFields = "id,ticketId";

    private readonly TicketingClient _client;
    private readonly InstanceCache _cache;
    private readonly TicketingOptions _options;
    private readonly LookupBudget? _budget;

    /// <param name="budget">
    /// Rows the number lookups may read, shared by every lookup made through this instance (a batch's, say); without
    /// one, each lookup may read Ticketing:MaxScanTickets.
    /// </param>
    public TicketRefs(TicketingClient client, InstanceCache cache, TicketingOptions options, LookupBudget? budget = null)
    {
        _client = client;
        _cache = cache;
        _options = options;
        _budget = budget;
    }

    /// <summary>What is left of a lookup budget, in rows read (see <see cref="MinPageCharge"/>).</summary>
    public sealed class LookupBudget(int rows)
    {
        public int Left { get; set; } = rows;

        /// <summary>What one request for a single ticket is charged, like a page of rows.</summary>
        public const int ReadCharge = MinPageCharge;
    }

    /// <summary>The ticket's UUID, or an error that says whether the ticket is certainly missing.</summary>
    public async Task<Guid> ResolveAsync(string? reference, int? timezoneOffset, CancellationToken cancellationToken, string paramName = "ticket")
    {
        string given = ToolValidation.RequireText(reference, paramName, 64);
        if (Guid.TryParse(given, out Guid uuid) && uuid != Guid.Empty)
        {
            return uuid;
        }

        (string? prefix, int number) = Parse(given, paramName);
        if (prefix is not null)
        {
            await CheckPrefixAsync(prefix, number, given, timezoneOffset, cancellationToken);
        }

        if (_cache.Numbers.TryGet(number, out Guid known))
        {
            return known;
        }

        NumberLookup found = await FindIdByNumberAsync(number, timezoneOffset, cancellationToken);
        return found switch
        {
            { Id: string id } => Guid.Parse(id),
            { Conclusive: true } => throw new McpException($"There is no ticket number {number}. Check the number, or search with list_tickets."),
            _ when _budget is not null => throw new McpException(
                $"Ticket number {number} wasn't found before the lookups for this batch used up what they may read together " +
                $"({_options.MaxScanTickets} tickets), so it may still exist. Name fewer tickets at a time, or select them with a filter."),
            _ => throw new McpException(
                $"Ticket number {number} wasn't found among the {_options.MaxScanTickets} tickets a lookup may read " +
                "(Ticketing:MaxScanTickets), so it may still exist. Find it with list_tickets (search or filters): a ticket listed " +
                "is remembered by its number, so then call again with the same number."),
        };
    }

    /// <summary>The number in 2213, #2213, or PREFIX-2213.</summary>
    internal static int ParseNumber(string value, string paramName = "ticket") => Parse(value, paramName).Number;

    /// <summary>The number in 2213 or #2213; null for anything else, a prefixed number included (its prefix needs checking).</summary>
    internal static int? BareNumber(string value)
    {
        Match m = NumberForm().Match(value.Trim());
        return m.Success && !m.Groups["prefix"].Success &&
               int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : null;
    }

    private static (string? Prefix, int Number) Parse(string value, string paramName)
    {
        Match m = NumberForm().Match(value.Trim());
        return m.Success && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? (m.Groups["prefix"].Success ? m.Groups["prefix"].Value : null, n)
            : throw new McpException($"'{paramName}' must be a ticket number (2213 or #2213) or a ticket UUID, not '{value}'.");
    }

    /// <summary>
    /// Takes a prefixed reference only when the prefix is this help desk's code, so a key from another system (OPS-123 in
    /// a tracker, say) can't act on this help desk's ticket 123. When the code isn't known (the API may not report one),
    /// the prefix can't be checked, so the reference is refused and the number alone asked for.
    /// </summary>
    private async Task CheckPrefixAsync(string prefix, int number, string given, int? timezoneOffset, CancellationToken cancellationToken)
    {
        string? code;
        try
        {
            code = (await _cache.GetInstanceAsync(_client, timezoneOffset, refresh: false, cancellationToken)).Code?.Trim();
        }
        catch (TicketingApiException)
        {
            code = null;
        }

        string bare = number.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(code))
        {
            throw new McpException(
                $"'{given}' has a prefix this server can't check against the help desk, so it could be another system's reference. " +
                $"If it is this help desk's ticket, pass the number alone ({bare}).");
        }

        if (!string.Equals(prefix, code, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"'{given}' isn't this help desk's ticket: its tickets are {code}-{bare} and so on. Check which system the reference is from.");
        }
    }

    // Up to ten ASCII digits; int.TryParse then keeps it within a ticket number's range.
    [GeneratedRegex(@"^(?:#|(?<prefix>[A-Za-z][A-Za-z0-9]{0,15})-)?\s*(?<n>[0-9]{1,10})$")]
    private static partial Regex NumberForm();

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
        LookupBudget budget = _budget ?? new LookupBudget(_options.MaxScanTickets);

        int searchSize = Math.Min(NumberSearchSize, budget.Left);
        ListResponse<Ticket> searched = await ListByNumberAsync(null, searchSize, search: number, timezoneOffset, cancellationToken);
        budget.Left -= searchSize;
        if (Match(searched.Items, number) is NumberLookup found)
        {
            return found;
        }

        if (budget.Left <= 0)
        {
            return new NumberLookup(null, false);
        }

        ListResponse<Ticket> newest = await ListByNumberAsync(null, 1, search: null, timezoneOffset, cancellationToken);
        budget.Left--; // one row: charging a full page here would leave a small budget no room to page at all
        if (newest.Items is { Count: 0 })
        {
            return new NumberLookup(null, true); // no tickets at all
        }

        if (newest.Items is not [Ticket top] || top.TicketNo is not int highest)
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

        int offset = Math.Max(0, likeliest - Math.Min(page, budget.Left) + 1);
        var visited = new HashSet<int>();
        while (budget.Left > 0 && visited.Add(offset))
        {
            int size = Math.Min(page, budget.Left);
            ListResponse<Ticket> window = await ListByNumberAsync(offset, size, search: null, timezoneOffset, cancellationToken);
            IReadOnlyList<Ticket> items = window.Items ?? [];
            budget.Left -= Math.Max(items.Count, MinPageCharge);

            if (Match(items, number) is NumberLookup hit)
            {
                return hit;
            }

            // A row without its number could be the ticket, so a page with one proves nothing about absence.
            List<int> numbers = items.Select(t => t.TicketNo).OfType<int>().ToList();
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
        List<Ticket> rows = tickets?.Where(t => t.TicketNo == number).ToList() ?? [];
        if (rows.Count == 0)
        {
            return null;
        }

        // A row with a usable ID wins over one without, wherever it appears.
        return rows.FirstOrDefault(t => Guid.TryParse(t.Id, out _)) is Ticket usable
            ? new NumberLookup(usable.Id, true)
            : throw new McpException($"Ticket number {number} exists, but the Ticketing API returned no usable ID for it. Try list_tickets with a search for it.");
    }

    private async Task<ListResponse<Ticket>> ListByNumberAsync(int? offset, int limit, int? search, int? timezoneOffset, CancellationToken cancellationToken)
    {
        ListResponse<Ticket> r = await _client.ListTicketsAsync(new TicketListQuery
        {
            Search = search?.ToString(CultureInfo.InvariantCulture),
            OrderBy = search is null ? "ticketId" : null,
            Order = search is null ? "DESC" : null,
            Select = NumberFields,
            Offset = offset is > 0 ? offset : null,
            Limit = limit,
            TimezoneOffset = timezoneOffset,
        }, cancellationToken);
        _cache.Seen(r.Items);
        return r;
    }

    /// <summary>The ticket's UUID when found; otherwise whether its absence is certain.</summary>
    private sealed record NumberLookup(string? Id, bool Conclusive);
}
