# Security overview

This server handles an instance-wide Ticketing API key and user-controlled ticket data. The API key is held in Key
Vault for hosted deployments or user secrets locally; it is never accepted from a tool call or returned to a client.
Ticket content is untrusted input to agents and is not included in metrics.

## Access controls

- Remote HTTP requires a single-tenant Entra access token with the configured delegated scope
  (`Entra:RequiredScope`, default `access_as_user`) or app role (`Entra:RequiredAppRole`, default
  `Ticketing.ReadWrite`).
- Optionally set `Entra:AcceptedClientApplicationIds` to a comma-separated allowlist of client application GUIDs.
  When set, only those client apps can call the server. `Entra:BlockedClientApplicationIds` is an explicit denylist
  and always takes precedence. Both v1 `appid` and v2 `azp` claims are checked. Empty accepted and blocked lists
  preserve the prior behavior of accepting any client with a valid authorized token.
- Assign users/groups or app identities to the server enterprise application as described in
  [Entra setup](setup-entra.md). Client ID filtering supplements token authorization; it does not replace it.
- Writes use the authenticated user's identity or the configured service account for app-only callers. Tools never
  accept an actor identity.
- Local stdio and loopback HTTP are unauthenticated and require an explicit service account. Local HTTP binds to
  loopback and rejects non-loopback, cross-origin, or mismatched-host requests. Local mode cannot start in Container
  Apps.

## Data and request safeguards

Tool inputs are validated before upstream requests. HTML is sanitized, names are resolved against instance metadata,
external requestor domains can be restricted, and unsafe paths, links, continuation headers, and unsupported custom
field values are refused. Writes that may have reached the vendor but did not return a response are reported as
unknown outcome to prevent unsafe retries.

Rate, concurrency, request-body, response-size, and timeout limits bound resource use. Per-caller and process-wide
upstream quotas protect the vendor's shared API-key limit. State is held in process memory, so the hosted app is
limited to one replica. File uploads are disabled by default and restricted to an explicitly configured local folder
over stdio only; see [stdio setup](stdio.md#file-uploads).

The HTTP service is stateless, HTTPS-only at ingress, non-root in a minimal container, and exposes only the separate
anonymous `/healthz` probe. Logs avoid ticket content and API keys. Application Insights receives low-cardinality
metrics only; see [operations](operations.md).

For implementation detail, residual risks, and deployment controls, see the
[security control reference](security-details.md).
