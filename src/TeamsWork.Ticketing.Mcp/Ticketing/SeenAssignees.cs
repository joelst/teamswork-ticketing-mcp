using System.Collections.Concurrent;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Everyone the server has seen as a ticket's assignee. Only help-desk staff can be assigned, and the instance's assignee
/// list can leave someone out, so these count as staff when telling a requestor's reply from an agent's. Kept for the
/// whole server, so get_queue (whatever it was asked for) and get_ticket_brief judge the same reply the same way. The
/// set is emptied rather than trimmed when it fills, which only costs some recognition until it fills again.
/// </summary>
public sealed class SeenAssignees
{
    private const int Capacity = 10_000;
    private readonly ConcurrentDictionary<string, TicketUser> _people = new(StringComparer.OrdinalIgnoreCase);

    public void Remember(TicketUser? user)
    {
        // By object ID when there is one, which is what decides who someone is; by email otherwise.
        string? key = Guid.TryParse(user?.Id, out Guid id) ? id.ToString("D") : string.IsNullOrWhiteSpace(user?.Email) ? null : user.Email.Trim();
        if (key is null)
        {
            return;
        }

        if (_people.Count >= Capacity)
        {
            _people.Clear();
        }

        _people[key] = user!;
    }

    public IReadOnlyCollection<TicketUser> All => [.. _people.Values];
}
