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

    public sealed record Result<T>(List<T> Matches, int Scanned, int? Total, bool Truncated);

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
        return new Result<T>(matches, scanned, total, Truncated: more || (total is int expected && seenInAll < expected));
    }

    /// <summary>The hint shown when a scan stopped before reading every ticket.</summary>
    public static string? TruncationHint<T>(Result<T> result) =>
        result.Truncated
            ? $"Only {result.Scanned} of {(result.Total is int t ? t.ToString(System.Globalization.CultureInfo.InvariantCulture) : "the")} tickets were checked: the " +
              "scan stops at Ticketing:MaxScanTickets, or when the API stops returning new tickets. It reads newest first, so " +
              "the rest are older: call again with createdBefore set to the day after the oldest day already seen (so none of " +
              "that day is skipped), or narrow it with the priority filter."
            : null;
}
