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

    public sealed record Result(List<Ticket> Matches, int Scanned, int? Total, bool Truncated);

    public static async Task<Result> RunAsync(
        TicketingClient client,
        TicketListQuery query,
        Func<Ticket, bool> match,
        int maxTickets,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var matches = new List<Ticket>();
        int scanned = 0;
        int? total = null;
        string? token = null;
        bool more;

        do
        {
            int size = Math.Min(pageSize, maxTickets - scanned);
            // The continuation token is preferred; offset paging covers a response that reports a total but no token.
            TicketListQuery page = query with
            {
                Limit = size,
                ContinuationToken = token,
                Offset = token is null && scanned > 0 ? scanned : null,
            };

            ListResponse<Ticket> r = await client.ListTicketsAsync(page, cancellationToken);
            IReadOnlyList<Ticket> items = r.Items ?? [];
            total ??= r.ItemCount;
            scanned += items.Count;
            matches.AddRange(items.Where(match));

            token = r.ContinuationToken;
            more = items.Count > 0 && (token is not null || (items.Count == size && total > scanned));
        }
        while (more && scanned < maxTickets);

        return new Result(matches, scanned, total, Truncated: more);
    }

    /// <summary>The hint shown when a scan stopped before reading every ticket.</summary>
    public static string? TruncationHint(Result result) =>
        result.Truncated
            ? $"Only the first {result.Scanned} of {(result.Total is int t ? t.ToString(System.Globalization.CultureInfo.InvariantCulture) : "the")} tickets were checked " +
              "(Ticketing:MaxScanTickets). Narrow the search with the date or priority filters to see the rest."
            : null;
}
