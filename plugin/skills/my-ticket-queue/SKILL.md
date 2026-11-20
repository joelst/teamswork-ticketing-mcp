---
name: my-ticket-queue
description: |
  Summarises the signed-in user's TeamsWork Ticketing help desk work: tickets assigned to them or raised by them,
  which ones are waiting on them or at risk of breaching their SLA, and what to do next. Read-only.
  Use when the user asks "what's on my plate", "my tickets", "what should I work on next", "am I behind on
  anything", "which of my tickets are about to breach", or "summarise my queue for standup".
license: MIT
metadata:
  author: TeamsWork Ticketing MCP
  version: "2.0.0"
---

# My ticket queue

Gives the user a prioritised view of their own tickets. This skill only reads; for changes, hand over to the
`ticket-update` skill.

## Tools this uses

From the TeamsWork Ticketing connector: `get_queue`, `get_ticket_brief` for a closer look at one ticket, and
`count_tickets` and `list_sla_risk` for team-wide questions.

## Workflow

1. **Get the queue in one call.** Call `get_queue` with no arguments: the user's open tickets, most urgent first,
   each with `waitingOn`, `idleDays` and `sla`. That is everything the table below needs.
   - Use `role: requestor` for "tickets I raised" and `role: either` for both.
   - If the `hint` says no user is signed in, tell the user the queue is the service account's, not theirs.
   - If the `hint` says the server stopped reading early, or that only the first rows have `waitingOn`, say so.

2. **Read the SLA state.** Each row's `sla` is `ok`, `frtEscalated`, `rtEscalated`, `frtBreached` or `rtBreached`
   (first response or resolution). A row without `sla` has an unknown state: report it as **not verified**. If the
   response has `slaTracking: "off"`, say SLA tracking is turned off rather than reporting nothing at risk.

3. **Read who each ticket is waiting on.** `waitingOn: assignee` means it's waiting on the user; `requestor` means
   it's waiting on the person who raised it; `thirdParty` on someone outside. `idleDays` is how long since anyone
   last did anything on it.

4. **Look closer only where it matters.** If the user asks about one ticket, or the next step isn't clear from the
   row, call `get_ticket_brief` with its number.

5. **Summarise** in the format below, in the order `get_queue` returned, and suggest the single next action for
   each of the top tickets.

## Output format

**Your queue: 7 open tickets (2 at SLA risk)**

| # | Ticket | Title | Priority | SLA | Waiting on | Idle | Next step |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | #1022 | Printer on floor 2 offline | Urgent | Resolution breached | You | 3 days | Confirm the fix with the requestor and resolve |
| 2 | #1035 | New starter laptop | Important | First response escalated | You | 1 day | Reply with the delivery date |
| 3 | #1040 | Shared mailbox access | Medium | OK | Requestor | 2 days | Nothing until they reply |

Then one line on the rest, for example: "4 more Low or Medium tickets, none at risk; the idlest is #0998, 12 days."

For a standup summary, give three short lines instead of the table: done, in progress, and blocked.
- **Done:** `get_queue` with `state: resolvedSince` and `since` set to yesterday (YYYY-MM-DD).
- **In progress:** the open queue, where `waitingOn` is `assignee`.
- **Blocked:** the open queue, where `waitingOn` is `requestor` or `thirdParty`.

## Rules

- Read-only: don't comment, reassign or change status from this skill. If the user wants to act on a ticket, use
  the `ticket-update` skill, which confirms each change.
- Keep ticket contents to what the summary needs; they can include customer contact details.
