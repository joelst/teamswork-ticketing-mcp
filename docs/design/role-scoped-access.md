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
by person, which is why reads for requesters need the index below. The live API differs from its spec in ways that
shape the design; see [Milestone 0 findings](#milestone-0-findings).

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

A ticket is **visible** to a requester when they are its requestor, assignee, or creator, or are in a people-picker
field marked `isSeeTicket`. A person on a ticket is identified by Entra object ID, except on tickets that arrived
by email, where the ID is the email address itself. So a person matches the caller when the ID is the caller's
`oid`, or when the ID is an email address equal to the caller's sign-in name (`upn`, which only an administrator
can change for a member; `email` and `preferred_username` aren't used, since they can be edited or unverified).
The `cc` list on comments doesn't grant visibility unless the spike below shows the app treats it that way.

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
fields and list columns. It is brought up to date when tools are used, not on a timer: an MCP server only acts when
a client calls it, so an idle server makes no requests (and can scale to zero).

**Row:** id, ticket number, title, status, priority, created and last-updated times, and the `oid`s of requestor,
assignee, creator, and `isSeeTicket` people. About 300 bytes, so 100,000 tickets is about 30 MB. `Index:MaxTickets`
(default 200,000) caps it; past the cap requester results say they are incomplete.

**Sync, on demand:**

| Step | Triggered by | Request |
| --- | --- | --- |
| Full build | The first call that needs the index after startup, and any call once the last full build is older than `Index:FullSyncHours` (default 6), which drops deleted tickets | Pages of 1,000 with `select` of the row fields: one request per 1,000 tickets |
| Incremental | Any tool call, once the index is older than `Index:RefreshAfterMinutes` (default 5) | `lastUpdateAfter` = the UTC date before the watermark (the API filters by whole days only), then rows kept only if `lastUpdatedOn` is after the watermark minus a 2-minute overlap; usually one request |
| Forced | `refresh: true` on a list, count, or search tool, at most once every 30 s for the whole index (as for the instance cache) | As incremental |
| Write-through | This server creating or changing a ticket | None: the API's response updates the row |

How a call treats the index's age:

| Index age | Call that reads the index | Any other call |
| --- | --- | --- |
| Under `Index:RefreshAfterMinutes` (5) | Answers from it | Nothing |
| Between that and `Index:MaxAgeMinutes` (15) | Answers from it at once; an incremental sync starts in the background | Starts the same background sync |
| Over `Index:MaxAgeMinutes`, or never built | Waits for the sync, up to 20 s; if it isn't done, answers from what there is and says the list may be out of date (or, before the first build, that it is still loading) | Starts the sync, doesn't wait |

So any tool call keeps the index warm while people are using the server, a list is never based on data more than
15 minutes old without saying so, and an idle server does nothing. Only one sync runs at a time; calls that arrive
during one share it. Syncs are charged to the server, not to the caller who triggered them, on the existing upstream
limiter with their own cap (`Index:MaxRequestsPerMinute`, default 10 of the 100), so they can't starve interactive
calls or use up one caller's share. A failed sync keeps the previous index; the next call past the threshold tries
again, no sooner than 30 s later.

**Freshness:** a ticket filed or reassigned in Teams appears in lists at the next sync: within 5 minutes while the
server is in use, and never silently after more than 15. A requester who has just filed one in Teams can ask with
`refresh: true`. Single-ticket reads never depend on the index, and the instance config and staff list keep their
own cache.

**Cold start:** the app scales to zero today, and the index lives in memory, so the first call after a cold start
builds it: one request per 1,000 tickets, a few seconds for most instances. That call waits up to 20 s and then
answers that the list is still loading rather than returning a partial list as complete. With on-demand sync,
scale-to-zero stays viable; `minReplicas = 1` only avoids the rebuild. A persisted snapshot (blob storage) is a later
option. The single-replica rule already in `app.bicep` means one index per deployment.

**Uses beyond requesters:** `list_my_tickets`, `count_tickets`, and `list_sla_risk` for staff can read the index
instead of scanning up to `MaxScanTickets` on each call, which removes their truncation and most of their upstream
cost. The existing scan stays as the fallback while the index is loading.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `Access:Mode` | `Open` | `RoleScoped` turns this design on (Entra mode only; startup refuses it with stdio or `--local`) |
| `Ticketing:ApiKeyIsReadOnly` | `false` | Hides write tools for everyone |
| `Index:Enabled` | `true` when `RoleScoped` | Staff tools may use it in `Open` mode too |
| `Index:RefreshAfterMinutes` | 5 | Age at which any tool call starts a background incremental sync |
| `Index:MaxAgeMinutes` | 15 | Age past which a call that reads the index waits for the sync first (up to 20 s) |
| `Index:FullSyncHours` | 6 | Age at which the next sync is a full rebuild, dropping deleted tickets |
| `Index:MaxRequestsPerMinute`, `Index:MaxTickets` | 10, 200,000 | The sync's share of the upstream limit; the most tickets indexed |

`app.bicep` gains `accessMode` and `apiKeyIsReadOnly` parameters; `New-EntraAppRegistrations.ps1` adds the
`Ticketing.Read` app role.

## Milestones

Each milestone ships behind `Access:Mode = Open` and keeps today's tests green.

**M0: spikes (no product code)**: done; see [Milestone 0 findings](#milestone-0-findings).
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
- `TicketIndex`: on-demand full build and incremental sync (single flight, triggered by tool calls through a request
  filter, no timer), the age thresholds, `refresh: true`, write-through, caps, cold-start behaviour, its own limiter
  share.
- Requester `list_tickets`, `count_tickets`, `find_similar_tickets` (API search, then filtered to visible tickets),
  `find_ticket_by_number`, `list_my_tickets`.
- Tests with a fake API and clock: sync picks up changes after the watermark, the overlap doesn't duplicate rows, a
  full sync drops deleted tickets, a sync failure keeps the old index; each age band behaves as in the table (no
  request under 5 minutes, a background sync between 5 and 15, a wait past 15); concurrent calls share one sync; an
  idle server makes no requests; loading answers "still loading".

**M4: requester writes**
- `create_ticket` (requestor forced, no assignee), `add_ticket_comment` (visible, never private),
  `update_ticket_status` (edge must authorize `requestor`).

**M5: staff tools on the index, docs, and rollout**
- `list_my_tickets`, `count_tickets`, `list_sla_risk` read the index when it's ready.
- `docs/security.md`, README configuration table, `app.bicep` parameters, `minReplicas` guidance.
- Rollout: deploy with `Open`, watch sync metrics (rows, last sync, sync requests a minute), then switch to
  `RoleScoped`.

## Milestone 0 findings

Checked on 2026-09-27 against the live instance (2,034 tickets) with read-only requests, and against the MCP SDK
2.2 documentation.

| Question | Finding | Effect on the design |
| --- | --- | --- |
| Does `select` return the index fields? | Yes: `id`, `ticketNo`, `status`, `priority`, `requestor`, `assignee`, `createdBy`, `customFields`, `createdOn`, `lastUpdatedOn`. A full build is 3 requests here. | As planned |
| How are custom fields keyed in lists? | By field **title** (`"Followers": []`), not by the ID the spec describes | The index finds `isSeeTicket` fields by title, and by ID where present |
| What identifies a person? | An Entra object ID, or the **email address** on tickets that arrived by email (requestor and creator). An empty assignee is `{"id":""}`. | Matching by `oid` or by `upn` for email IDs (see Tools per role) |
| Does `lastUpdatedOn` move? | Yes for public and private comments (within 1.5 s) and status changes (written about 0.8 s before the activity). Assignment wasn't observed directly. | Incremental sync on `lastUpdatedOn` holds; the 2-minute overlap covers the skew |
| Do date filters work? | **Only as a plain date.** `YYYY-MM-DD` filters; any time of day (the spec's own `YYYY-MM-DDTHH:mm:ss`, with or without `Z`) is silently ignored and every ticket comes back. "After" includes the named day; "before" excludes it. | Incremental sync asks by date and trims by timestamp. Fixed in today's tools (below). |
| Is the default list order by date? | No: 200 rows came back in neither created order. `orderBy=createdDateTime&order=DESC` sorts them. | Scans ask for that order, so offset pages don't shift and a scan that stops early can say where to continue; the index's full build will too |
| Which way does `timezone` shift date filters? | Opposite to the spec ("7 means GMT+7"): day D starts at `D 00:00Z + timezone hours` (checked at -12 to +14, "after" at or after, "before" at or before). Other endpoints follow the spec: instance SLA hours are shown at UTC + offset, and writes store an expected date at midnight in the offset sent. | The index sends `timezone=0`. `TicketDateFilters` holds the list rules. |
| How are expected dates stored? | As midnight in the offset of whoever set them (00:00Z when set at 0, 05:00Z at -5), so the setter's zone matters when filtering | Filters match them as calendar days in the instance's zone, boundary at noon of the day before |
| Are private activities flagged? | Yes, `isPrivate: true`, and a comment can be sent with it | Requesters get activities without them |
| What do attachments return? | Signed blob URLs, valid for about an hour | Attachment tools need the ticket check; a private activity's attachments are dropped with it |
| Anything else on activities? | `action` (`created`, `started`, `assigned`, `commented`, `closed`, ...) and a `cc` list | `cc` doesn't grant visibility until its meaning is known |
| `assignees.type` values | Only `teamsOwner` on this instance | Staff is "on the assignee list" whatever the type |
| Per-tool authorization in the SDK | `[Authorize]` on tools is supported, and the SDK filters list results by it (`FilterAuthorizedItemsAsync`), with policies from ASP.NET Core's policy provider | Tool layer as planned; M1 proves list and call with tests |

**Bugs in today's tools found by these checks**, now fixed (`TicketDateFilters`):
- Every date filter was sent with a time of day (a plain date was expanded to `T00:00:00`), so the API ignored it and
  returned every ticket, unmarked. Date filters now take plain dates only, and a time of day is refused.
- The filters were sent with the caller's offset, which the API applies in the opposite direction, so for US Central
  a day started 10 hours early. Created and updated filters now send the offset negated (taken on the filtered day,
  for daylight saving); expected dates are matched as calendar days in the instance's zone. Checked live: 60 of 60
  expected-date cases, and 18 of 20 when mixed with created filters for a due date set at UTC (documented).

## Risks and open questions

- The API key remains the real permission. Anything holding it bypasses all of this, so it stays in Key Vault and is
  used only by the server.
- The app may have visibility rules the API doesn't show (for example, private instances or department scoping).
  Deny by default covers what we can't see; the M0 spike lists what the live config exposes.
- Removing someone from the Team's owners takes up to the instance cache TTL to remove staff access.
- Name-only person lookups (`find the ticket Jane raised`) for requesters are limited to themselves.
- Index memory and the first full build scale with the instance's ticket count; `Index:MaxTickets` bounds both.
- Later: several instances, selected by URL (`/mcp/{instance}`), each with its own key, cache, and index.
