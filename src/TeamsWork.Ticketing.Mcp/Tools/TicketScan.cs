using System.Globalization;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Pages through <c>GET /tickets</c> and filters here, for questions the API has no filter for (assignee, requestor,
/// SLA state). Bounded by Ticketing:MaxScanTickets, so one tool call can't use up the vendor quota: with a narrow
/// 'select' a page holds up to 1000 tickets, so a scan is usually one request.
/// </summary>
internal static class TicketScan
{
    /// <summary>The API's largest page.</summary>
    public const int MaxApiPageSize = 1000;

    /// <summary>
    /// What a scan found. <paramref name="Oldest"/> and <paramref name="Newest"/> are the creation times of the oldest
    /// and newest tickets checked, for telling the agent where to continue when the scan stopped early.
    /// </summary>
    public sealed record Result<T>(List<T> Matches, int Scanned, int? Total, bool Truncated, DateTimeOffset? Oldest = null, DateTimeOffset? Newest = null);

    /// <summary>
    /// Reads tickets and keeps what <paramref name="pick"/> returns for each (null skips the ticket). Only the picked
    /// values are kept, not the tickets, so a scan of full tickets holds one page at a time rather than every match.
    /// </summary>
    public static async Task<Result<T>> RunAsync<T>(
        TicketingClient client,
        TicketListQuery query,
        Func<Ticket, T?> pick,
        int maxTickets,
        int pageSize,
        CancellationToken cancellationToken)
        where T : class
    {
        // Newest created first, which the API does only when asked: its default order isn't by date. The hint for a
        // scan that stops early depends on it, and a fixed order also keeps offset pages from shifting under the scan.
        if (query.OrderBy is null)
        {
            query = query with { OrderBy = "createdDateTime", Order = "DESC" };
        }

        // Each ticket's creation time is kept for that hint, so a narrowed 'select' asks for it too.
        if (query.Select is string select && !select.Split(',').Any(f => string.Equals(f.Trim(), "createdOn", StringComparison.Ordinal)))
        {
            query = query with { Select = select + ",createdOn" };
        }

        DateTimeOffset? oldest = null, newest = null;
        var matches = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        int read = 0;        // rows read: where the next offset page starts, since the API's offset counts rows
        int withoutId = 0;   // rows without an ID, which can't be told apart across pages
        bool repeated = false;
        int? total = null;
        string? token = null;
        bool more;

        do
        {
            int size = Math.Min(pageSize, maxTickets - read);
            // The continuation token is preferred; offset paging covers a response that has none.
            TicketListQuery page = query with
            {
                Limit = size,
                ContinuationToken = token,
                Offset = token is null && read > 0 ? read : null,
            };

            ListResponse<Ticket> r = await client.ListTicketsAsync(page, cancellationToken);
            IReadOnlyList<Ticket> items = r.Items ?? [];
            total ??= r.ItemCount;

            // Counted once each, so an API that repeats tickets across pages (or ignores offset) can't inflate results.
            // Rows without an ID aren't offered as matches (nothing can act on them).
            int before = scanned;
            foreach (Ticket t in items)
            {
                if (string.IsNullOrWhiteSpace(t.Id))
                {
                    withoutId++;
                }
                else if (!seen.Add(t.Id))
                {
                    repeated = true;
                }
                else
                {
                    scanned++;
                    if (DateTimeOffset.TryParse(t.CreatedOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset created))
                    {
                        oldest = oldest is DateTimeOffset o && o <= created ? o : created;
                        newest = newest is DateTimeOffset n && n >= created ? n : created;
                    }

                    if (pick(t) is T picked)
                    {
                        matches.Add(picked);
                    }
                }
            }

            // An empty page ends the scan, since paging on from it makes no progress (an API that keeps answering empty
            // pages with a token would loop). It is the end only if nothing says otherwise: a token, or a total not yet
            // proven, means more may exist, so the result is marked incomplete rather than complete.
            if (items.Count == 0)
            {
                more = r.ContinuationToken is not null || (total is int all && all > (repeated ? scanned : scanned + withoutId));
                break;
            }

            // A page that brings no ticket not already seen is no progress: the API is repeating itself (ignoring offset,
            // or a token that loops), so paging further can't reach the rest. Its rows aren't counted as read; the scan
            // stops, incomplete unless what was already proven reaches the total (a repeated last page after a complete
            // scan is the API's quirk, not a sign of more).
            bool progress = scanned > before || (read == 0 && items.All(x => string.IsNullOrWhiteSpace(x.Id)));
            if (!progress)
            {
                more = total is not int known || known > (repeated ? scanned : scanned + withoutId);
                break;
            }

            read += items.Count;
            token = r.ContinuationToken;

            // Completeness is only claimed on proof: distinct tickets seen, plus rows without an ID as long as the API
            // hasn't repeated itself (once it has, those rows could be repeats too, so only distinct tickets count). Raw rows
            // aren't proof: a page with a repeat and a new ticket would reach the total with one ticket still unseen.
            int proven = repeated ? scanned : scanned + withoutId;

            // Another page exists if the API says so (a token, or a total not yet proven). Without either, a full page
            // means there may be more; a short page is the end. The API may return fewer than asked for, so page fullness
            // alone never ends a scan that a total says isn't finished.
            more = token is not null || (total is int reported ? reported > proven : items.Count == size);
        }
        while (more && read < maxTickets);

        // Incomplete if more was expected, or if fewer tickets were proven seen than the API said there are.
        int seenInAll = repeated ? scanned : scanned + withoutId;
        return new Result<T>(matches, scanned, total, Truncated: more || (total is int expected && seenInAll < expected), oldest, newest);
    }

    /// <summary>
    /// The hint shown when a scan stopped before reading every ticket, saying exactly how to reach the rest. The scan
    /// reads newest created first, so the tickets not checked were created no later than the oldest one that was. The
    /// suggested createdBefore is the earliest date whose cut, as the next request would really send it (the planner
    /// decides the offset from the query's earliest day, so no separate local-day arithmetic can disagree with it),
    /// falls after that ticket: so nothing unchecked is past it. It helps only if the cut is also before the newest
    /// ticket checked (otherwise the next call reads the same tickets) and earlier than a createdBefore already given
    /// (otherwise it is the same query); each case that can't page by date says which it is.
    /// </summary>
    public static string? TruncationHint<T>(Result<T> result, TicketListQuery query, TimeZoneOffsetResolver zones)
    {
        if (!result.Truncated)
        {
            return null;
        }

        string checkedText =
            $"Only {result.Scanned} of {(result.Total is int t ? t.ToString(CultureInfo.InvariantCulture) : "the")} tickets were checked: the " +
            "scan stops at Ticketing:MaxScanTickets, or when the API stops returning new tickets.";
        if (result.Oldest is not DateTimeOffset oldest || result.Newest is not DateTimeOffset newest)
        {
            return checkedText + " Narrow it with the priority or date filters to see the rest.";
        }

        // The cut for a date is that date's midnight in some offset of at most 14 hours either way, so the first date
        // whose cut is after the oldest ticket is within a day of its UTC date.
        DateOnly utcDay = DateOnly.FromDateTime(oldest.UtcDateTime);
        DateOnly? next = null;
        for (int days = -1; days <= 2 && next is null; days++)
        {
            if (utcDay.DayNumber + days is int number && number >= DateOnly.MinValue.DayNumber && number <= DateOnly.MaxValue.DayNumber &&
                TicketDateFilters.CreatedBeforeCut(query, DateOnly.FromDayNumber(number), zones) > oldest)
            {
                next = DateOnly.FromDayNumber(number);
            }
        }

        string oldestText = $"the oldest checked was created at {oldest.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture)}";
        if (next is not DateOnly date)
        {
            return checkedText + " Narrow it with the priority filter or a search to see the rest.";
        }

        if (query.CreatedBefore is DateOnly given && date >= given)
        {
            return checkedText +
                $" They were read newest first and {oldestText}, but an earlier createdBefore than the '{Day(given)}' given would " +
                "skip tickets from that day that weren't checked: narrow it with the priority filter or a search instead.";
        }

        if (TicketDateFilters.CreatedBeforeCut(query, date, zones) >= newest)
        {
            return checkedText +
                $" They were read newest first and {oldestText}; every ticket checked is from within that day, so a date filter " +
                "would read the same tickets again: narrow it with the priority filter or a search instead.";
        }

        return checkedText +
            $" They were read newest first and {oldestText}. To continue, call again with createdBefore '{Day(date)}' and the " +
            "same other filters: it starts after that ticket, so none of the rest is skipped (a few are read again).";
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}