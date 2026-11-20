using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Workflow states, for the status filter on list_tickets and the transitions get_ticket_brief and update_ticket_status
/// offer. Default workflows name states by label (Open, Reopened, In Progress, Resolved, Closed); custom workflows by
/// state ID, with labels for display. Everything here is a convenience over what the API would decide anyway, so a
/// workflow this can't read never stops a request: it is passed on as given.
/// </summary>
internal static class WorkflowStatus
{
    public static readonly string[] DefaultStates = ["Open", "Reopened", "In Progress", "Resolved", "Closed"];

    // ---- list_tickets: one exact state -------------------------------------------------------------------------------

    /// <summary>
    /// The value to send as the API's statusId for a state named by ID or label. One rule, whatever shape the workflow
    /// settings take: a state's ID first, a default state's name counting as its ID (it is what that state's tickets
    /// carry), then a custom state's label, which must name one state. So every state can be chosen: a default one by
    /// name, a custom one by its ID, or by its label when no other state has it. When the instance's custom states can't
    /// be found, anything that isn't a default name is sent as given, since it may be a custom ID.
    /// </summary>
    public static string ResolveFilter(string value, JsonElement? instanceWorkflows, string paramName)
    {
        List<WorkflowState> custom = StatesIn(instanceWorkflows);
        WorkflowState? byId = custom.FirstOrDefault(s => Same(s.Id, value));
        if (byId?.Id is string id)
        {
            return id;
        }

        if (DefaultStates.FirstOrDefault(d => Same(d, value)) is string defaultState)
        {
            return defaultState;
        }

        var byLabel = custom.Where(s => Same(s.Label, value) && s.Id is not null).Select(s => (Id: s.Id!, Name: $"{s.Label} (state ID {s.Id})"))
            .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return byLabel.Count switch
        {
            1 => byLabel[0].Id,
            > 1 => throw new McpException(
                $"'{paramName}' '{value}' names more than one workflow state: {string.Join("; ", byLabel.Select(s => s.Name))}. " +
                "Pass the state ID to choose one, or isResolved for every open or every closed ticket."),
            _ when custom.Count > 0 => throw new McpException(
                $"'{paramName}' '{value}' isn't a workflow state. States: " +
                // A state whose ID is its label (the default graph's, live) is named once.
                string.Join(", ", DefaultStates.Concat(custom.Select(s => s.Label is null || Same(s.Label, s.Id) ? s.Label ?? s.Id! : $"{s.Label} ({s.Id})")).Distinct(StringComparer.OrdinalIgnoreCase)) +
                ". For every open or every closed ticket, use isResolved instead."),
            _ => value,
        };
    }

    /// <summary>
    /// The instance's workflows as states and the steps between them. The API returns the workflow editor's graph, which
    /// also carries node positions, colours, and automation rules (with people's names and addresses); an agent needs
    /// none of that. A custom state is shown with its ID, which update_ticket_status takes.
    /// </summary>
    public static JsonNode? Compact(JsonElement? workflows)
    {
        if (workflows is not JsonElement root)
        {
            return null;
        }

        List<JsonElement> graphs = GraphsIn(root);
        var result = new JsonArray();
        foreach (JsonElement graph in graphs)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (WorkflowState state in StatesIn(graph.GetProperty("nodes")))
            {
                labels[state.Id!] = state.Label ?? state.Id!;
            }

            var states = new JsonArray(labels.Select(kv => (JsonNode?)JsonValue.Create(
                string.Equals(kv.Key, kv.Value, StringComparison.Ordinal) ? kv.Value : $"{kv.Value} (ID {kv.Key})")).ToArray());

            // A step names its states by label, which is shorter, unless two states share it: then by label and ID, as above.
            var shared = labels.Values.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Endpoint(string id, string label) => shared.Contains(label) && !string.Equals(id, label, StringComparison.Ordinal) ? $"{label} (ID {id})" : label;
            var steps = new JsonArray();
            foreach (JsonElement edge in graph.GetProperty("edges").EnumerateArray())
            {
                // Only steps between states: the start node's edge carries the automation rules.
                if (Text(edge, "source") is not string from || Text(edge, "target") is not string to ||
                    !labels.TryGetValue(from, out string? fromLabel) || !labels.TryGetValue(to, out string? toLabel))
                {
                    continue;
                }

                var step = new JsonObject { ["from"] = Endpoint(from, fromLabel), ["to"] = Endpoint(to, toLabel) };
                if (Text(edge, "label") is string action)
                {
                    step["action"] = action;
                }

                if (edge.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("recordComment", out JsonElement comment) && comment.ValueKind == JsonValueKind.True)
                    {
                        step["needsComment"] = true;
                    }

                    if (data.TryGetProperty("authorizedUsers", out JsonElement roles) && roles.ValueKind == JsonValueKind.Array && roles.GetArrayLength() > 0)
                    {
                        step["roles"] = JsonNode.Parse(roles.GetRawText());
                    }
                }

                steps.Add(step);
            }

            result.Add(new JsonObject { ["states"] = states, ["steps"] = steps });
        }

        if (result.Count > 0)
        {
            return result;
        }

        // States in the ticket-workflow shape (id, label, nextSteps), or a shape this doesn't know: the states still help,
        // with an ID that differs from the label, as above, so a state sharing a label can still be named. Nothing else is
        // passed on unchecked.
        List<WorkflowState> known = StatesIn(workflows);
        return known.Count == 0
            ? null
            : new JsonArray(new JsonObject
            {
                ["states"] = new JsonArray(known.Select(s => (JsonNode?)JsonValue.Create(
                    s.Label is null || Same(s.Label, s.Id) ? s.Label ?? s.Id : $"{s.Label} (ID {s.Id})")).ToArray()),
            });
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    /// <summary>The instance's state names (labels), or the default workflow's when its settings name none.</summary>
    public static List<string> StateNames(JsonElement? workflows)
    {
        List<WorkflowState> found = StatesIn(workflows);
        if (found.Count == 0)
        {
            return [.. DefaultStates];
        }

        // A label more than one state has (two custom states, or a custom state named like a default one) can't choose
        // between them, so such a state is named with its ID too, which the status filter takes.
        Dictionary<string, int> owners = found.Select(s => (Id: s.Id!, Label: s.Label ?? s.Id!))
            .Concat(DefaultStates.Select(d => (Id: d, Label: d)))
            .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .GroupBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return found
            .Select(s => s.Label is string label && owners[label] > 1 && !Same(label, s.Id) ? $"{label} (ID {s.Id})" : s.Label ?? s.Id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The states found in the instance's workflow settings, whose shape the API doesn't document. The live API returns
    /// each workflow as a graph editor's nodes and edges: a state is a node (a "type" ending in Node, a string id, and a
    /// label under "data"; the start node has a blank label and isn't a state). An object shaped like a ticket's own
    /// workflow state (a string id and a nextSteps list) counts too, wherever it sits.
    /// </summary>
    internal static List<WorkflowState> StatesIn(JsonElement? workflows)
    {
        var found = new List<WorkflowState>();
        void Walk(JsonElement e, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in e.EnumerateArray())
                {
                    Walk(item, depth + 1);
                }
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    if (e.TryGetProperty("nextSteps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
                    {
                        string? label = e.TryGetProperty("label", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                        found.Add(new WorkflowState(id.GetString(), label, null, null));
                    }
                    else if (e.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
                             type.GetString()!.EndsWith("Node", StringComparison.OrdinalIgnoreCase) &&
                             e.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object &&
                             data.TryGetProperty("label", out JsonElement nodeLabel) && nodeLabel.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(nodeLabel.GetString()))
                    {
                        found.Add(new WorkflowState(id.GetString(), nodeLabel.GetString(), null, null));
                    }
                }

                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, depth + 1);
                }
            }
        }

        if (workflows is JsonElement root)
        {
            Walk(root, 0);
        }

        return found.Where(s => !string.IsNullOrWhiteSpace(s.Id)).DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- get_ticket_brief: where the ticket can go next ----------------------------------------------------------------

    /// <summary>One status change the ticket's workflow allows from its current state.</summary>
    internal sealed record NextStatus(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("transition")] string? Transition,
        [property: JsonPropertyName("requiresComment")] bool RequiresComment,
        [property: JsonPropertyName("resolves")] bool? Resolves,
        [property: JsonPropertyName("allowedRoles")] IReadOnlyList<string>? AllowedRoles);

    /// <summary>
    /// The instance's workflow settings when <paramref name="ticket"/> needs them to find its steps (it has a state but no
    /// workflow of its own, as the live API returns tickets); null otherwise, without a read.
    /// </summary>
    public static Task<JsonElement?> WorkflowsForAsync(Ticket ticket, InstanceCache cache, TicketingClient client, int? timezoneOffset, CancellationToken cancellationToken) =>
        ticket.Workflow is { Count: > 0 } || string.IsNullOrWhiteSpace(ticket.Status)
            ? Task.FromResult<JsonElement?>(null)
            : InstanceWorkflowsAsync(cache, client, timezoneOffset, cancellationToken);

    /// <summary>The instance's workflow settings (cached), or null when they can't be read: they only add to what's known.</summary>
    /// <summary>
    /// The instance's workflows (cached) when any of these tickets needs them to show its state's label: a custom
    /// workflow's ticket, or a status that isn't a default state's name. A default-workflow ticket names its state
    /// already, so a list of only those reads nothing.
    /// </summary>
    public static Task<JsonElement?> WorkflowsForLabelsAsync(IEnumerable<Ticket> tickets, InstanceCache cache, TicketingClient client, int? timezoneOffset, CancellationToken cancellationToken) =>
        tickets.Any(t => t.IsCustomWorkflow == true || (!string.IsNullOrWhiteSpace(t.Status) && !DefaultStates.Contains(t.Status.Trim(), StringComparer.OrdinalIgnoreCase)))
            ? InstanceWorkflowsAsync(cache, client, timezoneOffset, cancellationToken)
            : Task.FromResult<JsonElement?>(null);

    public static async Task<JsonElement?> InstanceWorkflowsAsync(InstanceCache cache, TicketingClient client, int? timezoneOffset, CancellationToken cancellationToken)
    {
        try
        {
            return (await cache.GetInstanceAsync(client, timezoneOffset, refresh: false, cancellationToken)).Workflows;
        }
        catch (TicketingApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// The states of the ticket's workflow. The API documents a 'workflow' list on each ticket, but the live API leaves it
    /// out, so the instance's workflow graph stands in: the one graph that has the ticket's state. With none, or more
    /// than one that could be the ticket's, nothing is claimed.
    /// </summary>
    internal static IReadOnlyList<WorkflowState>? StatesOf(Ticket ticket, JsonElement? instanceWorkflows)
    {
        if (ticket.Workflow is { Count: > 0 } own)
        {
            return own;
        }

        if (string.IsNullOrWhiteSpace(ticket.Status) || instanceWorkflows is not JsonElement root)
        {
            return null;
        }

        // A graph with a state of that ID is the ticket's before one with only a state of that label: a default ticket's
        // "Open" is the default graph's, even when a custom graph labels one of its states Open. Two exact matches, or
        // none and two by label, leave it unknown.
        List<List<WorkflowState>> graphs = GraphsIn(root).Select(GraphStates).ToList();
        List<List<WorkflowState>> byId = graphs.Where(states => states.Any(s => Same(s.Id, ticket.Status))).ToList();
        List<List<WorkflowState>> candidates = byId.Count > 0 ? byId : graphs.Where(states => Current(states, ticket.Status) is not null).ToList();
        return candidates is [List<WorkflowState> only] ? only : null;
    }

    /// <summary>
    /// Whether the ticket's state is one its resolvedStatus lists, which may name it by ID or by label: a custom
    /// workflow's ticket carries the state's ID as its status. Null when the response has no resolvedStatus.
    /// </summary>
    public static bool? IsResolved(Ticket ticket, JsonElement? instanceWorkflows = null)
    {
        if (ticket.ResolvedStatus is not { } resolved || string.IsNullOrWhiteSpace(ticket.Status))
        {
            return null;
        }

        if (resolved.Any(r => Same(r, ticket.Status)))
        {
            return true;
        }

        // A custom workflow's ticket carries the state's ID; when that state can't be found, a list of labels can't say.
        if (StatesOf(ticket, instanceWorkflows) is not { } states || Current(states, ticket.Status) is not { } current)
        {
            return ticket.IsCustomWorkflow == true ? null : false;
        }

        return resolved.Any(r => Same(r, current.Label));
    }

    /// <summary>The ticket's current state in its workflow, when exactly one state has its status as ID or label.</summary>
    private static WorkflowState? Current(IReadOnlyList<WorkflowState> states, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        List<WorkflowState> matches = states.Where(s => Same(s.Id, status)).ToList();
        if (matches.Count == 0)
        {
            matches = states.Where(s => Same(s.Label, status)).ToList();
        }

        return matches.DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList() is [WorkflowState only] ? only : null;
    }

    /// <summary>
    /// The transitions from the ticket's current state, each with the value update_ticket_status takes; null when the
    /// ticket's workflow or current state can't be read, so nothing is claimed.
    /// </summary>
    public static IReadOnlyList<NextStatus>? AllowedNext(Ticket ticket, JsonElement? instanceWorkflows = null)
    {
        if (StatesOf(ticket, instanceWorkflows) is not { } states || Current(states, ticket.Status) is not { NextSteps: { } steps })
        {
            return null;
        }

        return steps.Where(s => !string.IsNullOrWhiteSpace(s.TargetId)).Select(s =>
        {
            WorkflowState? target = states.FirstOrDefault(w => Same(w.Id, s.TargetId));
            return new NextStatus(
                StatusValue(ticket, s.TargetId!, target),
                target?.Label,
                s.TransitionLabel,
                s.RecordComment == true,
                ticket.ResolvedStatus is { } resolved ? resolved.Any(r => Same(r, s.TargetId) || Same(r, target?.Label)) : null,
                s.AuthorizedUsers is { Count: > 0 } roles ? roles : null);
        }).ToList();
    }

    /// <summary>
    /// The label of the ticket's current state, from its workflow (its own, or the instance's graph); its status as it is
    /// when the workflow doesn't say. A custom workflow's ticket carries the state's ID as its status.
    /// </summary>
    public static string? LabelOf(Ticket ticket, JsonElement? instanceWorkflows = null) =>
        StatesOf(ticket, instanceWorkflows) is { } states && Current(states, ticket.Status) is { Label: string label } && !string.IsNullOrWhiteSpace(label)
            ? label
            : ticket.Status;

    /// <summary>
    /// Whether the ticket is already in the state <paramref name="target"/> names, by state ID or label: a custom
    /// workflow's ticket carries its state ID as its status, so the text alone doesn't say. A target no state is named
    /// is taken as a transition's label, as <see cref="CheckTarget"/> takes it ("Close"), and the ticket is in it when
    /// such a transition leads to its current state.
    /// </summary>
    public static bool IsIn(Ticket ticket, string target, JsonElement? instanceWorkflows = null) =>
        Same(ticket.Status, target) ||
        (StatesOf(ticket, instanceWorkflows) is { } states && Current(states, ticket.Status) is { } current &&
         (Same(current.Id, target) || Same(current.Label, target) ||
          (!states.Any(s => Same(s.Id, target) || Same(s.Label, target)) &&
           current.NextSteps?.Any(n => Same(n.TransitionLabel, target)) != true &&   // a step of that name leads on from here
           states.Any(s => s.NextSteps?.Any(n => Same(n.TransitionLabel, target) && Same(n.TargetId, current.Id)) == true))));

    /// <summary>
    /// Checks a requested status change against the ticket's workflow and returns the value to send: a target given by
    /// state ID, state label, or transition label becomes what the API takes (the label in a default workflow, the state
    /// ID in a custom one). A change is refused only when the current state is known, has transitions, and none of them
    /// leads to the target, or when the transition requires a comment and none was given.
    /// </summary>
    public static string CheckTarget(Ticket ticket, string target, string? comment, JsonElement? instanceWorkflows = null)
    {
        if (StatesOf(ticket, instanceWorkflows) is not { } states || Current(states, ticket.Status) is not { } current)
        {
            return target;
        }

        if (current.NextSteps is not { Count: > 0 } steps)
        {
            // The instance's graph lists every edge, so a state with none there is final. A ticket's own workflow list
            // may leave steps out, so there the API decides.
            if (ticket.Workflow is { Count: > 0 } || current.NextSteps is null)
            {
                return target;
            }

            throw new McpException(
                $"'{current.Label ?? current.Id}' is a final state in this ticket's workflow: no step leads out of it, so it can't move to '{target}'.");
        }

        // The closest kind of match decides: the target state's ID, then its label, then a transition's label. So the
        // 'Closed' state isn't ambiguous with another state reached by a transition labelled 'Closed'; and a target that
        // names a state of the workflow means that state even when it isn't reachable from here, so it is refused rather
        // than taken for a transition of that name to somewhere else.
        bool namesState = states.Any(s => Same(s.Id, target) || Same(s.Label, target));
        var candidates = steps
            .Where(s => !string.IsNullOrWhiteSpace(s.TargetId))
            .Select(s => (Step: s, State: states.FirstOrDefault(w => Same(w.Id, s.TargetId))))
            .ToList();
        var matches = new Func<(WorkflowTransition Step, WorkflowState? State), bool>[]
            {
                m => Same(m.Step.TargetId, target),
                m => Same(m.State?.Label, target),
                m => !namesState && Same(m.Step.TransitionLabel, target),
            }
            .Select(tier => candidates.Where(tier).DistinctBy(m => m.Step.TargetId, StringComparer.OrdinalIgnoreCase).ToList())
            .FirstOrDefault(found => found.Count > 0) ?? [];

        IReadOnlyList<NextStatus> allowed = AllowedNext(ticket, instanceWorkflows) ?? [];
        string from = current.Label ?? current.Id ?? ticket.Status!;
        if (matches.Count == 0)
        {
            throw new McpException(
                $"'status' '{target}' isn't a step this ticket's workflow allows from '{from}'. Allowed: {Describe(allowed)}.");
        }

        if (matches.Count > 1)
        {
            throw new McpException(
                $"'status' '{target}' matches more than one step from '{from}': {Describe(allowed)}. Pass the state to move to.");
        }

        (WorkflowTransition step, WorkflowState? state) = matches[0];
        if (step.RecordComment == true && comment is null)
        {
            throw new McpException(
                $"Moving this ticket from '{from}' to '{state?.Label ?? step.TargetId}' requires a comment: pass 'comment' with a note on why.");
        }

        return StatusValue(ticket, step.TargetId!, state);
    }

    /// <summary>The workflow editor graphs (objects with nodes and edges) in the instance's workflow settings.</summary>
    private static List<JsonElement> GraphsIn(JsonElement root)
    {
        var graphs = new List<JsonElement>();
        void Find(JsonElement e, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("nodes", out JsonElement n) && n.ValueKind == JsonValueKind.Array &&
                    e.TryGetProperty("edges", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
                {
                    graphs.Add(e);
                    return;
                }

                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Find(p.Value, depth + 1);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in e.EnumerateArray())
                {
                    Find(item, depth + 1);
                }
            }
        }

        Find(root, 0);
        return graphs;
    }

    /// <summary>One graph's states, with each state's steps taken from the edges leaving it.</summary>
    private static List<WorkflowState> GraphStates(JsonElement graph)
    {
        List<WorkflowState> nodes = StatesIn(graph.GetProperty("nodes"));
        var ids = new HashSet<string>(nodes.Select(n => n.Id!), StringComparer.Ordinal);
        var steps = new Dictionary<string, List<WorkflowTransition>>(StringComparer.Ordinal);
        foreach (JsonElement edge in graph.GetProperty("edges").EnumerateArray())
        {
            if (Text(edge, "source") is not string from || Text(edge, "target") is not string to || !ids.Contains(from) || !ids.Contains(to))
            {
                continue;
            }

            JsonElement data = edge.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.Object ? d : default;
            bool hasData = data.ValueKind == JsonValueKind.Object;
            List<string>? roles = hasData && data.TryGetProperty("authorizedUsers", out JsonElement r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : null;
            bool comment = hasData && data.TryGetProperty("recordComment", out JsonElement c) && c.ValueKind == JsonValueKind.True;
            if (!steps.TryGetValue(from, out List<WorkflowTransition>? list))
            {
                steps[from] = list = [];
            }

            list.Add(new WorkflowTransition(to, Text(edge, "label"), roles, comment, null));
        }

        return nodes.Select(n => n with { NextSteps = steps.GetValueOrDefault(n.Id!) ?? [] }).ToList();
    }

    private static string StatusValue(Ticket ticket, string targetId, WorkflowState? target) =>
        ticket.IsCustomWorkflow == true ? targetId : target?.Label ?? targetId;
    private static string Describe(IReadOnlyList<NextStatus> allowed) =>
        allowed.Count == 0
            ? "(none)"
            : string.Join("; ", allowed.Select(a =>
                a.Status + (a.Label is not null && !Same(a.Label, a.Status) ? $" ({a.Label})" : "") +
                (a.Transition is not null ? $" via '{a.Transition}'" : "") + (a.RequiresComment ? ", needs a comment" : "")));

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
