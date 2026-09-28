---
name: my-ticket-queue
description: |
  Summarises the signed-in user's TeamsWork Ticketing help desk work: tickets assigned to them or raised by them,
  which ones are at risk of breaching their SLA, and what to do next. Read-only.
  Use when the user asks "what's on my plate", "my tickets", "what should I work on next", "am I behind on
  anything", "which of my tickets are about to breach", or "summarise my queue for standup".
license: MIT
metadata:
  author: TeamsWork Ticketing MCP
  version: "1.0.0"
---

# My ticket queue

Gives the user a prioritised view of their own tickets. This skill only reads; for changes, hand over to the
`ticket-update` skill.

## Tools this uses

From the TeamsWork Ticketing connector: `whoami`, `list_my_tickets`, `list_sla_risk`, `get_ticket_context`, and
`count_tickets` for team-wide questions.

## Workflow

1. **Confirm whose queue it is.** Call `whoami`. It names the account changes are attributed to and that
   `list_my_tickets` matches against. If it says no user is signed in (a service account), tell the user their
   queue can't be read as them, and stop.

2. **Get their tickets.** Call `list_my_tickets`:
   - `role: assignee` by default. Use `requestor` for "tickets I raised" and `either` for both.
   - Unresolved only, unless the user asks about closed work (`includeResolved: true`).
   - If `truncated` is true, the server stopped reading early. Say so, and offer to continue with the
     `createdBefore` date its `hint` names.

3. **Find the SLA risks.** Call `list_sla_risk` and keep the tickets that are also in the user's list.
   - `isFrtBreached` or `isRtBreached` means the first-response or resolution target has already passed.
   - `isFrtEscalated` or `isRtEscalated` means the ticket was escalated for it.
   - If the hint says SLA tracking is turned off, say that instead of reporting nothing at risk.

4. **Look closer only where it matters.** For the top few tickets (breached first, then Urgent and Important, then
   the oldest), call `get_ticket_context` to see the latest activity: is it waiting on the user, or on the requestor?

5. **Summarise** in the format below, most urgent first, and suggest the single next action for each of the top
   tickets.

## Output format

**Your queue: 7 open tickets (2 at SLA risk)**

| # | Ticket | Title | Priority | SLA | Waiting on | Next step |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | #1022 | Printer on floor 2 offline | Urgent | Resolution breached | You | Confirm the fix with the requestor and resolve |
| 2 | #1035 | New starter laptop | Important | First response escalated | You | Reply with the delivery date |
| 3 | #1040 | Shared mailbox access | Medium | OK | Requestor | Nothing until they reply |

Then one line on the rest, for example: "4 more Low or Medium tickets, none at risk; oldest is #0998 from 12 days
ago."

For a standup summary, give three short lines instead of the table: done, in progress, and blocked. For "done",
call `list_my_tickets` with `includeResolved: true` and take the resolved or closed tickets whose `lastUpdatedOn` is
in the last day. The tool can't filter by resolution date, so say it's based on the last update.

## Rules

- Read-only: don't comment, reassign or change status from this skill. If the user wants to act on a ticket, use
  the `ticket-update` skill, which confirms each change.
- "Waiting on" comes from the latest activity: a comment from the requestor after the user's last one means it's
  waiting on the user.
- Keep ticket contents to what the summary needs; they can include customer contact details.
