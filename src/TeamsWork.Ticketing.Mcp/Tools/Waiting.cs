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
    // Each agent's email, and whether any agent with it is known by email alone (no object ID).
    private readonly Dictionary<string, bool> _agentEmails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _agentIds = [];
    private readonly IReadOnlySet<string> _system;

    private TicketPeople(IReadOnlySet<string> system) => _system = system;

    /// <summary>
    /// Whether the instance's assignee list was read. Without it, someone writing who isn't the ticket's assignee or
    /// requestor may be staff or may not, so their reply can't say who the ticket waits on.
    /// </summary>
    public bool HasRoster { get; private init; }

    public static TicketPeople From(Instance? instance, TicketingOptions options)
    {
        var people = new TicketPeople(options.SystemAccountSet()) { HasRoster = instance?.Assignees?.Peoples is not null };
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
        bool hasId = Guid.TryParse(id, out Guid oid);
        if (hasId)
        {
            _agentIds.Add(oid);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            string key = email.Trim();
            _agentEmails[key] = !hasId || _agentEmails.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Whether the author is help-desk staff, by the rules of <see cref="IsPerson"/>: an author with an object ID is an
    /// agent's only by that ID, so someone else writing from a staff member's address isn't taken for them; the email
    /// decides for an author without one, or for an agent known by email alone.
    /// </summary>
    public bool IsAgent(TicketUser? user)
    {
        if (user is null)
        {
            return false;
        }

        bool hasId = Guid.TryParse(user.Id, out Guid oid);
        if (hasId && _agentIds.Contains(oid))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(user.Email) && _agentEmails.TryGetValue(user.Email.Trim(), out bool emailOnly) && (!hasId || emailOnly);
    }

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
    /// <param name="ResolutionUnknown">
    /// Why <paramref name="WaitingOn"/> is null when it is: true when whether the ticket is resolved isn't known, false
    /// when the history read doesn't reach back far enough to tell.
    /// </param>
    /// <param name="RosterUnknown">
    /// True when <paramref name="WaitingOn"/> is null because the latest reply's author couldn't be placed: the staff
    /// list wasn't read, and they are neither the ticket's assignee nor its requestor.
    /// </param>
    public sealed record State(string? WaitingOn, DateTimeOffset? WaitingSince, int? IdleDays, Reply? LastPublic, bool ResolutionUnknown = false, bool RosterUnknown = false);

    /// <param name="By">requestor, agent, or other (someone else on the requestor's side, such as a cc).</param>
    public sealed record Reply(string By, DateTimeOffset? On, string? Text);

    /// <param name="history">The ticket's activities, newest first, as the API returns them.</param>
    /// <param name="complete">Whether <paramref name="history"/> reaches back to the ticket's creation.</param>
    /// <param name="statusLabel">
    /// The ticket's state as its workflow labels it (see <see cref="WorkflowStatus.LabelOf"/>): a custom workflow's ticket
    /// carries a state ID as its status, which says nothing about waiting.
    /// </param>
    /// <param name="resolved">Whether the ticket is resolved; null when the response didn't say (no resolvedStatus).</param>
    public static State Compute(Ticket t, IReadOnlyList<Activity> history, TicketPeople people, bool? resolved, bool complete, DateTimeOffset now, string? statusLabel = null)
    {
        DateTimeOffset? created = Parse(t.CreatedOn);
        // From the creation only when no person has done anything since; a latest activity without a time leaves it unknown,
        // as an older time would make a ticket just handled look idle.
        Activity? lastHuman = history.FirstOrDefault(a => IsByPerson(a, people));
        int? idle = lastHuman is not null ? (Parse(lastHuman.When) is DateTimeOffset at ? Days(at, now) : null)
            : complete && created is DateTimeOffset c ? Days(c, now)
            : null;

        Activity? lastReply = history.FirstOrDefault(a => IsByPerson(a, people) && a.IsPrivate != true && !string.IsNullOrWhiteSpace(a.Comment));
        Reply? reply = lastReply is null ? null : new Reply(Side(lastReply.Author, t, people), Parse(lastReply.When), lastReply.Comment);

        // Unsaid, a default workflow's state still tells (Resolved and Closed are resolved, the others aren't), by the
        // ticket's own status; a custom workflow's doesn't, whatever its state is labelled, and then who it waits on is
        // left unknown rather than taken for an open ticket's.
        string? raw = t.IsCustomWorkflow == true ? null : Blank(t.Status);
        bool? isResolved = resolved ?? (raw is null ? null
            : DefaultResolved.Contains(raw) ? true
            : WorkflowStatus.DefaultStates.Contains(raw, StringComparer.OrdinalIgnoreCase) ? false
            : null);
        if (isResolved == true)
        {
            return new State(None, null, idle, reply);
        }

        if (isResolved is null)
        {
            return new State(null, null, idle, reply, ResolutionUnknown: true);
        }

        // A state named for waiting says on whom: someone outside ("Awaiting vendor support", checked first, as such a
        // state can name a role too), the help desk itself ("Waiting on assignee", "Pending agent"), or otherwise the
        // requestor ("Waiting on customer").
        if (Blank(statusLabel ?? t.Status) is string status && WaitingState().IsMatch(status))
        {
            string on = ThirdPartyState().IsMatch(status) ? ThirdParty : AssigneeState().IsMatch(status) ? Assignee : Requestor;
            return new State(on, null, idle, reply);
        }

        // Reopened by a person since the latest reply (a requestor's Reopen button sends no comment): the ticket is back
        // with the help desk. Any reply the history read doesn't reach is older still, so it holds either way.
        int replyAt = lastReply is null ? history.Count : IndexOf(history, lastReply);
        if (history.Take(replyAt).FirstOrDefault(a => IsByPerson(a, people) && string.Equals(a.Action?.Trim(), "reopened", StringComparison.OrdinalIgnoreCase)) is Activity reopen)
        {
            return new State(Assignee, Parse(reopen.When), idle, reply);
        }

        if (reply is null)
        {
            // Nobody has replied since the ticket was created: it waits on its assignee. With only its latest history read,
            // an older reply may still decide, so it is unknown.
            return complete ? new State(Assignee, created, idle, null) : new State(null, null, idle, null);
        }

        if (reply.By == "other" && !people.HasRoster)
        {
            // Without the staff list, an author who is neither assignee nor requestor may be an agent.
            return new State(null, null, idle, reply, RosterUnknown: true);
        }

        return new State(reply.By == "agent" ? Requestor : Assignee, reply.On, idle, reply);
    }

    /// <summary>requestor for the ticket's requestor (checked first, since staff raise tickets too), agent for the help desk, other for anyone else.</summary>
    public static string Side(TicketUser? author, Ticket t, TicketPeople people) =>
        TicketPeople.Same(author, t.Requestor) ? "requestor"
        : people.IsAgent(author) || TicketPeople.Same(author, t.Assignee) ? "agent"
        : "other";

    private static bool IsByPerson(Activity a, TicketPeople people) => !a.IsAutomation && !people.IsSystem(a.Author);

    private static int IndexOf(IReadOnlyList<Activity> history, Activity a)
    {
        for (int i = 0; i < history.Count; i++)
        {
            if (ReferenceEquals(history[i], a))
            {
                return i;
            }
        }

        return history.Count;
    }

    public static int Days(DateTimeOffset from, DateTimeOffset now) => Math.Max(0, (int)Math.Floor((now - from).TotalDays));

    public static DateTimeOffset? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t)
            ? t.ToUniversalTime()
            : null;

    private static readonly HashSet<string> DefaultResolved = new(StringComparer.OrdinalIgnoreCase) { "Resolved", "Closed" };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A workflow state named for waiting ("Waiting on requestor", "Pending customer", "Awaiting vendor").
    [GeneratedRegex(@"\b(waiting|awaiting|pending)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WaitingState();

    [GeneratedRegex(@"\b(vendor|supplier|third[- ]?party|partner|external)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThirdPartyState();

    [GeneratedRegex(@"\b(assignee|agent|technician|engineer|staff|support|internal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssigneeState();
}
