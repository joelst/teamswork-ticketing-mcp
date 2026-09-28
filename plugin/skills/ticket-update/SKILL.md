---
name: ticket-update
description: |
  Makes changes to TeamsWork Ticketing help desk tickets as the signed-in user: creating a ticket, adding a public
  reply or a private note, attaching links, reassigning, changing priority or other fields, and resolving, closing
  or reopening. Confirms every change with the user before making it.
  Use when the user asks to "reply to ticket 1234", "add a note", "resolve this ticket", "close it as fixed",
  "reopen", "assign it to Sam", "raise the priority", "log a ticket for", "attach this link", or "create a ticket".
license: MIT
metadata:
  author: TeamsWork Ticketing MCP
  version: "0.1.0"
---

# Ticket updates

Every change here is recorded as the signed-in user; the tools don't accept another person as the actor. Show the
user exactly what will change and wait for a yes before each write.

## Tools this uses

From the TeamsWork Ticketing connector: `find_ticket_by_number`, `get_ticket`, `get_ticket_context`,
`find_similar_tickets`, `get_instance`, `list_tag_categories`, and the writes `create_ticket`, `update_ticket`,
`update_ticket_status`, `assign_ticket`, `add_ticket_comment` and `add_ticket_link_attachments`.

## Workflow

1. **Find the ticket.** A number the user quotes (1234, #1234) goes to `find_ticket_by_number`; the other tools
   need the UUID it returns in `id`. If the number isn't found, say what the tool said: it can mean the ticket may
   still exist beyond what the lookup reads.

2. **Read before writing.** Call `get_ticket_context` (or `get_ticket` for a status change) so the change fits the
   ticket's current state.

3. **Draft the change** using the section below for its kind, and show it:
   > On **#1041 VPN drops every few minutes**, I'll add a **public reply** (the requestor will see it):
   > "Thanks, we've replaced the switch on floor 3. Can you confirm the VPN stays connected?"
   > Go ahead?

4. **Make it only after a yes.** Then report the result, including who it was recorded as (`actedAs`).

5. **If a write says it may have gone through** (a timeout or server error), read the ticket again with
   `get_ticket_context` and check before trying again. Don't repeat a comment or status change blindly.

## Kinds of change

**Reply or note** (`add_ticket_comment`). Ask whether the requestor should see it, unless that's clear: a note for
colleagues is `isPrivate: true`; a reply to the requestor is `isPrivate: false`. Use `comment` for plain text, or
`commentHtml` for lists and links.

**Status** (`update_ticket_status`). Pick the target from the allowed next steps in `get_ticket`'s `workflow` and
`status` fields; only those work. Default workflows use Open, Reopened, In Progress, Resolved and Closed; custom
workflows use the state ID. When resolving, ask for the resolution (`fixed`, `cannotResolve` or `cancelled`) if
the user didn't say, and offer to record a closing note in `comment`; some transitions require one.

**Assignment** (`assign_ticket`). The new assignee must be on the assignee list (`get_instance` with
`section: assignees`). Use their email; if the user gives a name that matches more than one person, ask which.

**Fields** (`update_ticket`). Pass only the fields that change. `expectedDate` is a plain YYYY-MM-DD date. `tags`
replaces the whole tag list, so include existing tags that should stay. Custom fields are keyed by their title or
ID from `get_instance` (`section: customFields`).

**Links** (`add_ticket_link_attachments`). Up to 20 links per call, each with a URL and a caption, plus an optional
comment. This attaches links, not files.

**New ticket** (`create_ticket`).
1. Call `find_similar_tickets` with the proposed title first. If a match is the same problem, suggest commenting
   on it instead.
2. Draft the title, description, priority and, if known, assignee and tags. The requestor defaults to the
   signed-in user; for someone else, use their email.
3. After creating it, check the response for `warning`. The ticket exists even when a warning says setting its
   priority failed: don't create it again; set the priority with `update_ticket` on the returned `id`.

## Output format

After each change, one line: "Done: public reply added to #1041 as Pat Example." After several, a short list in the
same form.

## Rules

- One confirmation per change. A "yes" covers the change you showed, not later ones.
- Don't paste ticket contents, customer details or internal notes anywhere outside the ticket unless the user asks.
- If the user asks to act as someone else, explain that changes are always recorded as them.
