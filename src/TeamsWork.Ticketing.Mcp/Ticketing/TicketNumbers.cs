using System.Collections.Concurrent;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// The UUID of every ticket number the server has seen in a response, so a tool given a number (2213, #2213) usually
/// needs no lookup requests. Numbers aren't reused, so an entry stays right; the map is emptied rather than trimmed when
/// it fills, which only costs lookups.
/// </summary>
public sealed class TicketNumbers
{
    private const int Capacity = 50_000;
    private readonly ConcurrentDictionary<int, Guid> _ids = new();

    public void Remember(Ticket? ticket)
    {
        if (ticket?.TicketNo is int number && Guid.TryParse(ticket.Id, out Guid id) && id != Guid.Empty)
        {
            if (_ids.Count >= Capacity)
            {
                _ids.Clear();
            }

            _ids[number] = id;
        }
    }

    public void Remember(IEnumerable<Ticket>? tickets)
    {
        foreach (Ticket ticket in tickets ?? [])
        {
            Remember(ticket);
        }
    }

    public bool TryGet(int number, out Guid id) => _ids.TryGetValue(number, out id);
}
