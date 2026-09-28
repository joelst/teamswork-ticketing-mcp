---
name: ticket-triage
description: |
  Triages new or unassigned TeamsWork Ticketing help desk tickets: reads each one, checks for duplicates, and
  proposes a priority, an assignee and tags for the user to approve before anything changes.
  Use when the user asks to "triage the queue", "triage new tickets", "what came in today", "who should take
  ticket 1234", "sort out the unassigned tickets", or "check this ticket for duplicates".
license: MIT
metadata:
  author: TeamsWork Ticketing MCP
  version: "0.1.0"
---

# Ticket triage

Works through recent tickets that need a decision and proposes, for each, what should happen. Nothing changes until
the user approves it.

## Tools this uses

From the TeamsWork Ticketing connector: `list_tickets`, `find_ticket_by_number`, `get_ticket_context`,
`find_similar_tickets`, `get_instance`, `list_tag_categories`, `count_tickets`, and for approved changes
`update_ticket`, `assign_ticket` and `add_ticket_comment`.

## Workflow

1. **Find what to triage.**
   - A ticket the user names by number (such as 1234): look it up with `find_ticket_by_number`. Every other tool
     needs the UUID it returns in `id`, never the number.
   - Otherwise, list recent unresolved tickets with `list_tickets`: `isResolved: false`, `orderBy: createdDateTime`,
     and a `createdAfter` date (default to the last 2 days, as YYYY-MM-DD). Use `select:
     id,ticketId,title,priority,assignee,requestor,createdOn,tags` to keep it small.
   - The API can't filter by assignee, so pick out the tickets with no assignee yourself. `count_tickets` with
     `groupBy: assignee` shows how many are "(unassigned)" if the user wants the size of the backlog first.
   - If `continuationToken` comes back, there are more; say how many you looked at and offer to continue.

2. **Read each ticket.** Call `get_ticket_context` for its details, recent activity and attachments in one call.
   Don't propose anything from the title alone.

3. **Check for duplicates.** Call `find_similar_tickets` with the ticket's title. A match with a high score about
   the same problem is a likely duplicate: say so, and propose commenting on the older ticket rather than working
   both. Leave out the ticket itself if it comes back as a match.

4. **Work out a proposal.**
   - **Priority:** Low, Medium, Important or Urgent. Base it on what the ticket says (outage, many people affected,
     a deadline) and say why in a few words.
   - **Assignee:** only someone from the assignee list (`get_instance` with `section: assignees`). If the ticket
     doesn't make the right person clear, say so rather than guessing.
   - **Tags:** only tags that exist (`list_tag_categories`).
   - **Missing information:** if the requestor needs to be asked something, draft the question.

5. **Show the proposals and wait.** Present one table, then ask which rows to apply (all, some, or edited).

6. **Apply only what was approved.**
   - Priority, tags or several fields together: `update_ticket` with just those fields. Tags given to
     `update_ticket` replace the ticket's tags, so include the ones it already has that should stay.
   - Assignee alone: `assign_ticket` with their email.
   - A note about the decision: `add_ticket_comment` with `isPrivate: true`, so the requestor doesn't see internal
     reasoning. A question for the requestor goes in a comment with `isPrivate: false`, and only when approved.
   - Report each result. Every write returns `actedAs`: the change is recorded as the signed-in user.

## Output format

| Ticket | Title | Proposed priority | Proposed assignee | Tags | Duplicate? | Why |
| --- | --- | --- | --- | --- | --- | --- |
| #1041 | VPN drops every few minutes | Important | Sam Example | Network | No | Several people affected since this morning |
| #1043 | Can't sign in to portal | Medium | (ask) | Access | Likely #1038 | Same error text as #1038 |

After the table: "Reply with the rows to apply, for example 'apply 1041, and put 1043 on #1038'."

## Rules

- Never change a ticket without the user's go-ahead in this conversation, even when the proposal looks obvious.
- If a write reports that it may have gone through (a timeout or server error), check the ticket with
  `get_ticket_context` before trying again, so nothing is done twice.
- Tickets can contain customer names and contact details. Show only what the triage needs, and don't copy ticket
  contents into other documents unless the user asks.
