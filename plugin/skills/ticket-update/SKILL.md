---
name: ticket-update
description: |
  Makes changes to TeamsWork Ticketing help desk tickets as the signed-in user: creating a ticket, adding a public
  reply or a private note, attaching links, reassigning, changing priority or other fields, and resolving, closing
  or reopening, one ticket at a time or many at once. Confirms every change with the user before making it.
  Use when the user asks to "reply to ticket 1234", "add a note", "resolve this ticket", "close it as fixed",
  "reopen", "assign it to Sam", "raise the priority", "log a ticket for", "attach this link", "create a ticket", or
  "close all the weekly check tickets".
license: MIT
metadata:
  author: TeamsWork Ticketing MCP
  version: "2.0.0"
---

# Ticket updates

Every change here is recorded as the signed-in user; the tools don't accept another person as the actor. Show the
user exactly what will change and wait for a yes before each write.

## Tools this uses

From the TeamsWork Ticketing connector: `get_ticket_brief`, `find_similar_tickets`, `get_instance`,
`list_tag_categories`, `preview_bulk_update`, and the writes `create_ticket`, `update_ticket`,
`update_ticket_status`, `assign_ticket`, `add_ticket_comment`, `add_ticket_link_attachments` and `bulk_update`.

## Workflow

1. **Name the ticket as the user did.** Every tool's `ticket` takes the number they quote (1234 or #1234).
   If a tool says a number isn't found, pass its message on: it can mean the ticket may still exist beyond what the
   lookup reads.

2. **Read before writing, unless the user has said it all.** Call `get_ticket_brief` so the change fits the ticket's
   current state. It includes `allowedNextStatuses`, the states the ticket can move to next, and `waitingOn`. If the
   user has already given both the action and its content ("reply to #1041 saying the switch is replaced"), you can
   draft it straight away; the tool refuses a step the workflow doesn't allow.

3. **Draft the change** using the section below for its kind, and show it:
   > On **#1041 VPN drops every few minutes**, I'll add a **public reply** (the requestor will see it):
   > "Thanks, we've replaced the switch on floor 3. Can you confirm the VPN stays connected?"
   > Go ahead?

   A reply and a status change on the same ticket can be shown, and confirmed, together.

4. **Make it only after a yes.** Each write answers with a short acknowledgment (`ok`, `ticketNo`, what `changed`,
   and `actedAs`, who it was recorded as). Report that; there's no need to read the ticket again.

5. **If a write says it may have gone through** (a timeout or server error), read the ticket again with
   `get_ticket_brief` and check before trying again. Don't repeat a comment or status change blindly.

## Kinds of change

**Reply or note** (`add_ticket_comment`). Ask whether the requestor should see it, unless that's clear: a note for
colleagues is `isPrivate: true`; a reply to the requestor is `isPrivate: false`. The tool requires one or the other.
Use `comment` for plain text, or `commentHtml` for lists and links.

**Status** (`update_ticket_status`). Pick the target from `allowedNextStatuses` and pass its `status`; only those
work, and the tool refuses any other with the list. Its `comment` is visible to the requestor, so use it for a reply
that goes with the change; put a private note in `add_ticket_comment` instead. If the entry has
`requiresComment: true`, the step needs that visible comment. When the entry has `resolves: true`, ask for the
resolution (`fixed`, `cannotResolve` or `cancelled`) if the user didn't say.

**Assignment** (`assign_ticket`). The new assignee must be on the assignee list (`get_instance` with
`section: assignees`). Use their email; if the user gives a name that matches more than one person, ask which.

**Fields** (`update_ticket`). Pass only the fields that change. `expectedDate` is a plain YYYY-MM-DD date. To add or
remove a tag, use `addTags` or `removeTags`, which keep the ticket's other tags; `tags` replaces the whole list.
Custom fields are keyed by their title or ID from `get_instance` (`section: customFields`).

**Links** (`add_ticket_link_attachments`). Up to 20 links per call, each with a URL and a caption, plus an optional
comment and the required `isPrivate`. This attaches links, not files.

**Several tickets at once** (`preview_bulk_update`, then `bulk_update`). For one change to many tickets, up to 50:
1. Call `preview_bulk_update` with the tickets by number (`tickets`) or a `filter` (such as
   `titlePattern: "Weekly check:*"`), the change in `set` (status, resolution, assignee, priority, tags, due date),
   and any `comment` with its required `isPrivate`. It changes nothing.
2. Show the user the preview: how many tickets change (`count`), the change (`to`), each ticket's current values
   (`from`), and any that are `blocked` or `unchanged` and why.
3. After a yes, call `bulk_update` with the `confirmToken` alone. Report `applied`, and any `skipped` (changed since
   the preview) or `failed`. If it returns a `resumeToken`, call `bulk_update` with it as the `confirmToken` to do
   the rest (after a minute, if the hint says the quota ran out).

**New ticket** (`create_ticket`).
1. Call `find_similar_tickets` with the proposed title first. If a match is the same problem, suggest commenting
   on it instead.
2. Draft the title, description, priority and, if known, assignee and tags. The requestor defaults to the
   signed-in user; for someone else, use their email.
3. After creating it, check the response for `warning`. The ticket exists even when a warning says setting its
   priority failed: don't create it again; set the priority with `update_ticket` on the returned `ticketNo`.

## Output format

After each change, one line: "Done: public reply added to #1041 as Pat Example." After several, a short list in the
same form; after a bulk update, the count applied and the tickets skipped or failed.

## Rules

- One confirmation per change. A "yes" covers the change you showed, not later ones; a bulk preview counts as one
  change.
- Don't paste ticket contents, customer details or internal notes anywhere outside the ticket unless the user asks.
- If the user asks to act as someone else, explain that changes are always recorded as them.
