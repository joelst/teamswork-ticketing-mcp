using System.Globalization;
using System.Text.RegularExpressions;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Who is who on the help desk, for telling a requestor's reply from an agent's: the agents are the instance's assignee
/// list, the service account, and whoever else a tool knows to be staff (the caller, the assignees it has seen), since
/// the list can leave someone out. System accounts (the help desk's mailbox, a no-reply sender, the automation author)
/// aren't people at all.
/// </summary>
internal sealed class TicketPeople
{
    private readonly HashSet<string> _agentEmails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _agentIds = [];
    private readonly IReadOnlySet<string> _system;

    private TicketPeople(IReadOnlySet<string> system) => _system = system;

    public static TicketPeople From(Instance? instance, TicketingOptions options)
    {
        var people = new TicketPeople(options.SystemAccountSet());
        foreach (Persona p in instance?.Assignees?.Peoples ?? [])
        {
            people.AddAgent(p.Id, p.Email);
        }

        people.AddAgent(options.ServiceAccount?.Id, options.ServiceAccount?.Email);
        return people;
    }

    /// <summary>The help desk's people, from the cached instance settings; just the service account if they can't be read.</summary>
    public static async Task<TicketPeople> LoadAsync(InstanceCache cache, TicketingClient client, TicketingOptions options, int? timezoneOffset, CancellationToken cancellationToken)
    {
        Instance? instance;
        try
        {
            instance = await cache.GetInstanceAsync(client, timezoneOffset, refresh: false, cancellationToken);
        }
        catch (TicketingApiException)
        {
            instance = null;
        }

        return From(instance, options);
    }

    /// <summary>Adds people known to be staff: the caller, and assignees seen on tickets.</summary>
    public TicketPeople WithAgents(IEnumerable<TicketUser?> agents)
    {
        foreach (TicketUser? a in agents)
        {
            AddAgent(a?.Id, a?.Email);
        }

        return this;
    }

    private void AddAgent(string? id, string? email)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            _agentEmails.Add(email.Trim());
        }

        if (Guid.TryParse(id, out Guid oid))
        {
            _agentIds.Add(oid);
        }
    }

    public bool IsAgent(TicketUser? user) =>
        user is not null &&
        ((!string.IsNullOrWhiteSpace(user.Email) && _agentEmails.Contains(user.Email.Trim())) ||
         (Guid.TryParse(user.Id, out Guid oid) && _agentIds.Contains(oid)));

    /// <summary>
    /// An author that isn't a person: a configured system account (by address or by the part before the @), or one with
    /// neither an email nor an Entra object ID, as automation rules write. People do appear with an object ID and no
    /// email (a reply from Teams, say), so those count as people.
    /// </summary>
    public bool IsSystem(TicketUser? user)
    {
        string? email = string.IsNullOrWhiteSpace(user?.Email) ? null : user.Email.Trim().ToLowerInvariant();
        if (email is null)
        {
            return !Guid.TryParse(user?.Id, out _);
        }

        int at = email.LastIndexOf('@');
        return _system.Contains(email) || (at > 0 && _system.Contains(email[..at]));
    }

    /// <summary>
    /// Whether a ticket's person is the one with <paramref name="personId"/> and <paramref name="personEmail"/>. The object
    /// ID decides when the ticket has one, so a ticket naming someone else's ID with this person's email (or the reverse)
    /// isn't counted as theirs; the email decides only for people recorded without an ID, or in the email-to-ticket form
    /// where the ID is the email. A person who is themselves in the email form (a service account without an object ID, or
    /// an outside requestor) can only be matched by email.
    /// </summary>
    public static bool IsPerson(TicketUser? user, string personId, string personEmail)
    {
        if (user is null)
        {
            return false;
        }

        if (string.Equals(personId.Trim(), personEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email.Trim(), personEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        string? id = string.IsNullOrWhiteSpace(user.Id) ? null : user.Id.Trim();
        bool sameEmail = !string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email.Trim(), personEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        if (id is null || string.Equals(id, user.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return sameEmail || string.Equals(id, personId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Compared as object IDs, so one written another way still matches.
        return Guid.TryParse(id, out Guid theirs) && Guid.TryParse(personId, out Guid wanted) ? theirs == wanted : string.Equals(id, personId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two recorded people are the same person, by the rules of <see cref="IsPerson"/>.</summary>
    public static bool Same(TicketUser? user, TicketUser? other) =>
        other is not null && !string.IsNullOrWhiteSpace(other.Email)
            ? IsPerson(user, string.IsNullOrWhiteSpace(other.Id) ? other.Email : other.Id, other.Email)
            : other is not null && Guid.TryParse(other.Id, out Guid theirs) && Guid.TryParse(user?.Id, out Guid mine) && theirs == mine;
}

/// <summary>
/// Who a ticket is waiting on, worked out from its latest history (spec section 8): nobody once it is resolved; the
/// requestor (or a third party) in a state named for waiting; otherwise whoever didn't write the latest public reply by
/// a person. Automation, system accounts and private notes don't count as replies. A ticket nobody has replied to waits
/// on its assignee; when the history read doesn't reach back to the ticket's creation, that can't be told, so it is
/// left unknown (null) rather than guessed.
/// </summary>
internal static partial class Waiting
{
    /// <summary>Activities read to find the latest reply; one request either way.</summary>
    public const int HistoryWindow = 20;

    public const string Assignee = "assignee", Requestor = "requestor", ThirdParty = "thirdParty", None = "none";

    /// <param name="WaitingSince">When the ticket started waiting, when known: the reply it waits after, or its creation.</param>
    /// <param name="IdleDays">Whole days since a person last did anything on it, of any visibility.</param>
    /// <param name="LastPublic">The latest public reply by a person, if any was read.</param>
    public sealed record State(string? WaitingOn, DateTimeOffset? WaitingSince, int? IdleDays, Reply? LastPublic);

    /// <param name="By">requestor, agent, or other (someone else on the requestor's side, such as a cc).</param>
    public sealed record Reply(string By, DateTimeOffset? On, string? Text);

    /// <param name="history">The ticket's activities, newest first, as the API returns them.</param>
    /// <param name="complete">Whether <paramref name="history"/> reaches back to the ticket's creation.</param>
    /// <param name="statusLabel">
    /// The ticket's state as its workflow labels it (see <see cref="WorkflowStatus.LabelOf"/>): a custom workflow's ticket
    /// carries a state ID as its status, which says nothing about waiting.
    /// </param>
    public static State Compute(Ticket t, IReadOnlyList<Activity> history, TicketPeople people, bool resolved, bool complete, DateTimeOffset now, string? statusLabel = null)
    {
        DateTimeOffset? created = Parse(t.CreatedOn);
        Activity? lastHuman = history.FirstOrDefault(a => IsByPerson(a, people));
        int? idle = Parse(lastHuman?.When) is DateTimeOffset at ? Days(at, now)
            : complete && created is DateTimeOffset c ? Days(c, now)
            : null;

        Activity? lastReply = history.FirstOrDefault(a => IsByPerson(a, people) && a.IsPrivate != true && !string.IsNullOrWhiteSpace(a.Comment));
        Reply? reply = lastReply is null ? null : new Reply(Side(lastReply.Author, t, people), Parse(lastReply.When), lastReply.Comment);

        if (resolved)
        {
            return new State(None, null, idle, reply);
        }

        if (Blank(statusLabel ?? t.Status) is string status && WaitingState().IsMatch(status))
        {
            return new State(ThirdPartyState().IsMatch(status) ? ThirdParty : Requestor, null, idle, reply);
        }

        if (reply is null)
        {
            // Nobody has replied since the ticket was created: it waits on its assignee. With only its latest history read,
            // an older reply may still decide, so it is unknown.
            return complete ? new State(Assignee, created, idle, null) : new State(null, null, idle, null);
        }

        return new State(reply.By == "agent" ? Requestor : Assignee, reply.On, idle, reply);
    }

    /// <summary>requestor for the ticket's requestor (checked first, since staff raise tickets too), agent for the help desk, other for anyone else.</summary>
    public static string Side(TicketUser? author, Ticket t, TicketPeople people) =>
        TicketPeople.Same(author, t.Requestor) ? "requestor"
        : people.IsAgent(author) || TicketPeople.Same(author, t.Assignee) ? "agent"
        : "other";

    private static bool IsByPerson(Activity a, TicketPeople people) => !a.IsAutomation && !people.IsSystem(a.Author);

    public static int Days(DateTimeOffset from, DateTimeOffset now) => Math.Max(0, (int)Math.Floor((now - from).TotalDays));

    public static DateTimeOffset? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t)
            ? t.ToUniversalTime()
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A workflow state named for waiting ("Waiting on requestor", "Pending customer", "Awaiting vendor").
    [GeneratedRegex(@"\b(waiting|awaiting|pending)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WaitingState();

    [GeneratedRegex(@"\b(vendor|supplier|third[- ]?party|partner|external)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThirdPartyState();
}
