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
        int read = 0;
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
            // Rows without an ID aren't offered as matches (nothing can act on them), but they are rows, and the API's
            // total counts rows, so paging and completeness are measured in rows read.
            int before = scanned;
            foreach (Ticket t in items.Where(x => !string.IsNullOrWhiteSpace(x.Id) && seen.Add(x.Id)))
            {
                scanned++;
                if (pick(t) is T picked)
                {
                    matches.Add(picked);
                }
            }

            // A page that brings no ticket not already seen is no progress: the API is repeating itself (ignoring offset,
            // or a token that loops), so paging further can't reach the rest. Its rows aren't counted as read; the scan
            // stops, incomplete if the API said there was more.
            bool progress = items.Count == 0 || scanned > before || (read == 0 && items.All(x => string.IsNullOrWhiteSpace(x.Id)));
            if (!progress)
            {
                more = true;
                break;
            }

            read += items.Count;
            token = r.ContinuationToken;
            // Another page exists if the API says so (a token, or a total not yet reached). Without either, a full page
            // means there may be more; a short page is the end. The API may return fewer than asked for, so page fullness
            // alone never ends a scan that a total says isn't finished.
            more = items.Count > 0 && (token is not null || (total is int known ? known > read : items.Count == size));
        }
        while (more && read < maxTickets);

        // Incomplete if more was expected, or if fewer rows were read than the API said there are.
        return new Result<T>(matches, scanned, total, Truncated: more || (total is int expected && read < expected));
    }

    /// <summary>The hint shown when a scan stopped before reading every ticket.</summary>
    public static string? TruncationHint<T>(Result<T> result) =>
        result.Truncated
            ? $"Only {result.Scanned} of {(result.Total is int t ? t.ToString(System.Globalization.CultureInfo.InvariantCulture) : "the")} tickets were checked: the " +
              "scan stops at Ticketing:MaxScanTickets, or when the API stops returning new tickets. Narrow it with the date or " +
              "priority filters to see the rest."
            : null;
}
