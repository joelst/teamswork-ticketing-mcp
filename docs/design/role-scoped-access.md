# Role-scoped access on a single instance

Status: proposed. Scope: the hosted HTTP server in Entra mode, one Ticketing instance. stdio and `--local` keep
today's behaviour (one service account, full access).

## Why

The Ticketing API authenticates with one instance API key, which can read and change every ticket. The API knows
nothing about the person behind a request, so today every signed-in user can do everything the key can. The
Teams app itself limits people by role; this design has the server apply the same limits, deny by default, before
any request reaches the API.

## What the API gives us to decide with

From `GET /instance` (checked on the live instance):

| Setting | Meaning | Used for |
| --- | --- | --- |
| `assignees.peoples` (with `assignees.type`, e.g. `teamsOwner`) | The people who work tickets; the app fills it from the Team's owners | Who is **staff** |
| `workflows.ticket[].edges[].data.authorizedUsers` (`owner`, `assignee`, `requestor`) | Who may make each status transition | Which transitions a requester may make |
| People-picker custom fields with `isSeeTicket: true` (e.g. Followers) | People named there can see the ticket | Who else may see a ticket |

From the ticket and activity models: `requestor`, `assignee`, `createdBy`, `customFields` (people pickers hold
`{id,name,email}`), and `isPrivate` on activities and comments ("visible only to internal agents").

From `GET /tickets`: `select` (return only named fields), `lastUpdateAfter`, `limit` up to 1000. There is no filter
by person, which is why reads for requesters need the index below.

No Graph permission is needed: the app already mirrors the Team's owners into the assignee list.

## Roles

| Role | Who | Resolved from |
| --- | --- | --- |
| Staff | A delegated caller whose `oid` is in the assignee list | Token `oid` + cached instance |
| Requester | Any other delegated caller in the tenant | Token |
| Agent (read-write) | App-only token with `Ticketing.ReadWrite` (as today); acts as the service account | Token `roles` |
| Agent (read-only) | App-only token with a new `Ticketing.Read` app role | Token `roles` |

The role is resolved once per request into a scoped `AccessContext` (role, caller `oid`, instance generation). The
assignee list comes from the existing instance cache, so a change in the Team's owners takes effect within
`Ticketing:InstanceCacheSeconds` (300 s by default). A caller whose staff membership can't be checked (instance
unreadable) is treated as a requester, never as staff.

A new setting turns this on: `Access:Mode` = `Open` (today's behaviour, and the default so existing deployments
don't change on upgrade) or `RoleScoped`. A read-only API key (`Ticketing:ApiKeyIsReadOnly = true`) removes every
write tool for everyone, whatever the role.

## Tools per role

| Tool | Staff | Requester | Agent RW | Agent RO |
| --- | --- | --- | --- | --- |
| `whoami`, `get_instance`, `list_tag_categories` | yes | yes (`get_instance`: fields, workflow, and no assignee emails) | yes | yes |
| `list_tickets`, `count_tickets`, `find_similar_tickets`, `find_ticket_by_number` | all tickets | visible tickets, from the index | all | all |
| `get_ticket`, `get_ticket_context` | all | visible tickets | all | all |
| `list_ticket_activities`, `list_ticket_attachments`, `list_activity_attachments` | all | visible tickets; private activities left out | all | all |
| `list_my_tickets` | yes | yes (theirs) | yes | yes |
| `list_sla_risk` | yes | no | yes | yes |
| `create_ticket` | yes | yes: requestor is always the caller; no assignee | yes | no |
| `add_ticket_comment` | yes | on visible tickets; never private | yes | no |
| `update_ticket_status` | yes | on visible tickets, only transitions whose `authorizedUsers` include `requestor` | yes | no |
| `update_ticket`, `assign_ticket`, `add_ticket_link_attachments` | yes | no | yes | no |

A ticket is **visible** to a requester when their `oid` is its requestor, assignee, or creator, or is in a
people-picker field marked `isSeeTicket`.

## Enforcement

Two layers, both required; hiding a tool is not the control.

1. **Tool layer.** Each tool method carries a policy (`Staff`, `AnyCaller`, `Writer`, ...). The SDK's authorization
   filters (already enabled) drop tools the caller may not use from `tools/list` and refuse `tools/call` for them.
   Milestone 0 confirms the SDK evaluates an async, per-request policy in stateless mode; if it doesn't, a
   request filter does the same with the same policy objects.
2. **Row layer.** One `TicketAccess` service answers "may this caller see / comment on / move this ticket":
   - single-ticket tools fetch the ticket and check the fetched copy, which is authoritative (the index isn't used
     for these), and answer a ticket the caller can't see exactly like a missing one, so its existence doesn't leak;
   - activity and attachment tools check the owning ticket (`list_activity_attachments` reads `ticketId` from the
     response) and drop `isPrivate` activities for requesters;
   - list, search, and count tools for requesters go through the index;
   - requester writes are rewritten or refused before the API call (`requestor` forced to the caller, `isPrivate`
     forced false, transitions checked against the workflow edge).

Every refusal names the rule ("only staff can assign tickets"), never other people's data.

## The ticket visibility index

Because the API can't filter by person, the server keeps a small in-memory index of every ticket's visibility
fields and list columns, refreshed in the background.

**Row:** id, ticket number, title, status, priority, created and last-updated times, and the `oid`s of requestor,
assignee, creator, and `isSeeTicket` people. About 300 bytes, so 100,000 tickets is about 30 MB. `Index:MaxTickets`
(default 200,000) caps it; past the cap requester results say they are incomplete.

**Sync:**

| Step | When | Request |
| --- | --- | --- |
| Full build | Startup, and every `Index:FullSyncMinutes` (default 360) to drop deleted tickets | Pages of 1,000 with `select` of the row fields: one request per 1,000 tickets |
| Incremental | Every `Index:SyncSeconds` (default 60) | `lastUpdateAfter` = last watermark minus a 2-minute overlap |
| Write-through | After this server creates or changes a ticket | None: the API's response updates the row |

Sync runs as its own caller on the existing upstream limiter, capped at `Index:MaxRequestsPerMinute` (default 10 of
the 100), so it can't starve interactive calls. A failed sync keeps the previous index and retries with backoff.

**Freshness:** a ticket filed or reassigned in Teams appears in requester lists within about a minute. Single-ticket
reads never depend on the index. The instance config and staff list keep their own cache.

**Cold start:** the app scales to zero today. Until the first full build finishes, requester list and search calls
wait up to 20 s and then answer that the list is still loading, rather than returning a partial list as complete.
Deployments using `RoleScoped` should set `minReplicas = 1`; a persisted snapshot (blob storage) is a later option.
The single-replica rule already in `app.bicep` means one index per deployment.

**Uses beyond requesters:** `list_my_tickets`, `count_tickets`, and `list_sla_risk` for staff can read the index
instead of scanning up to `MaxScanTickets` on each call, which removes their truncation and most of their upstream
cost. The existing scan stays as the fallback while the index is loading.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `Access:Mode` | `Open` | `RoleScoped` turns this design on (Entra mode only; startup refuses it with stdio or `--local`) |
| `Ticketing:ApiKeyIsReadOnly` | `false` | Hides write tools for everyone |
| `Index:Enabled` | `true` when `RoleScoped` | Staff tools may use it in `Open` mode too |
| `Index:SyncSeconds`, `Index:FullSyncMinutes`, `Index:MaxRequestsPerMinute`, `Index:MaxTickets` | 60, 360, 10, 200,000 | |

`app.bicep` gains `accessMode` and `apiKeyIsReadOnly` parameters; `New-EntraAppRegistrations.ps1` adds the
`Ticketing.Read` app role.

## Milestones

Each milestone ships behind `Access:Mode = Open` and keeps today's tests green.

**M0: spikes (no product code)**
- Per-tool async authorization policies with the MCP SDK 2.2 in stateless mode: listing and calling.
- On the live API: does `lastUpdatedOn` move for comments, status changes, and assignment? Does `select` return
  `customFields` and `createdBy`? What values does `assignees.type` take besides `teamsOwner`? Are private
  activities returned, and flagged, by `GET /activities`?
- The answers decide the sync design (if comments don't bump `lastUpdatedOn`, visibility still holds, because only
  people fields grant visibility, but list ordering needs a note).

**M1: roles and the tool layer**
- `AccessContext`, role resolution, `Access:Mode`, `Ticketing:ApiKeyIsReadOnly`, the `Ticketing.Read` app role.
- Policies on every tool; the tool matrix above as a table-driven test (each role sees exactly its tools, and calls
  to hidden ones are refused).

**M2: the row layer for single tickets**
- `TicketAccess`; `get_ticket`, `get_ticket_context`, activities and attachments (private activities dropped);
  "not found" for invisible tickets.
- Tests: a requester can't read, comment on, or move someone else's ticket by ID or number; staff can.

**M3: the index**
- `TicketIndex` (background service): full build, incremental sync, write-through, caps, cold-start behaviour, its
  own limiter share.
- Requester `list_tickets`, `count_tickets`, `find_similar_tickets` (API search, then filtered to visible tickets),
  `find_ticket_by_number`, `list_my_tickets`.
- Tests with a fake API: sync picks up changes after the watermark, the overlap doesn't duplicate rows, a full sync
  drops deleted tickets, a sync failure keeps the old index, loading answers "still loading".

**M4: requester writes**
- `create_ticket` (requestor forced, no assignee), `add_ticket_comment` (visible, never private),
  `update_ticket_status` (edge must authorize `requestor`).

**M5: staff tools on the index, docs, and rollout**
- `list_my_tickets`, `count_tickets`, `list_sla_risk` read the index when it's ready.
- `docs/security.md`, README configuration table, `app.bicep` parameters, `minReplicas` guidance.
- Rollout: deploy with `Open`, watch sync metrics (rows, last sync, sync requests a minute), then switch to
  `RoleScoped`.

## Risks and open questions

- The API key remains the real permission. Anything holding it bypasses all of this, so it stays in Key Vault and is
  used only by the server.
- The app may have visibility rules the API doesn't show (for example, private instances or department scoping).
  Deny by default covers what we can't see; the M0 spike lists what the live config exposes.
- Removing someone from the Team's owners takes up to the instance cache TTL to remove staff access.
- Name-only person lookups (`find the ticket Jane raised`) for requesters are limited to themselves.
- Index memory and the first full build scale with the instance's ticket count; `Index:MaxTickets` bounds both.
- Later: several instances, selected by URL (`/mcp/{instance}`), each with its own key, cache, and index.
