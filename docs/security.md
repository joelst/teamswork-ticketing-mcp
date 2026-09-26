# Security model

## Assets and trust boundaries

- **Ticketing API key**: grants full read/write access to the Ticketing instance. Stored only in Azure Key Vault
  (`Ticketing--ApiKey`, purge protection on). The container app reads it through a Key Vault secret reference with a
  user-assigned managed identity (`Key Vault Secrets User`). Locally: user secrets or Key Vault via
  `DefaultAzureCredential`. It is appended to the outbound query string inside `TicketingClient` only; the default
  HttpClient logger (which prints full URLs) is removed, exceptions never carry the URL, and tests assert the key
  never appears in error text.
- **Ticket data** (names, emails, descriptions): only flows to authenticated, assigned callers and is never logged.
  Logs contain method, path, status, and timing.
- **Caller identity**: Entra ID access tokens for audience `api://<clientId>`.

## Controls

| Area | Control |
| --- | --- |
| Authentication | Microsoft.Identity.Web validates issuer (single tenant), audience, lifetime, and signature against Entra keys. Tokens with neither `scp` nor `roles` are rejected. |
| Authorization | Policy `McpCaller`: `scp` contains `access_as_user` **or** `roles` contains `Ticketing.ReadWrite`. Service principal has *assignment required*, so unassigned users cannot even obtain a token. |
| Discovery | RFC 9728 protected-resource metadata and `WWW-Authenticate` challenge, so clients learn the authorization server without guessing; no anonymous MCP endpoint. |
| Actor integrity | Acting user comes from token claims (or the configured service account for app-only tokens). No tool accepts a `user` argument; the integration test asserts this for every tool. |
| Transport | Stateless Streamable HTTP: no server sessions, nothing to hijack, horizontal scaling safe. HTTPS-only ingress (`allowInsecure: false`); TLS terminated by Container Apps. |
| Input validation | Every tool validates arguments before they reach the upstream URL/body: UUIDs, enums, `YYYY-MM-DD` dates, datetime filters, page-size caps, URL schemes (http/https only) for links, custom-field IDs must be GUIDs. Errors are returned as tool errors with actionable text, never stack traces. |
| Rate limiting | Process-wide sliding window matching the vendor's 100 requests / 60 s, bounded retries with backoff on 429/5xx. |
| Container | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`: distroless-style, non-root `app` user, no shell/package manager. The `-extra` variant adds the time zone data the server needs (and ICU, unused). Diagnostics disabled. 0.25 vCPU / 0.5 GiB. |
| Registry / pulls | ACR admin user disabled; the container app pulls with its managed identity (`AcrPull`). |
| Pipeline | Workload-identity-federation service connection (no stored cloud secrets). `NuGetAudit` fails restore on known vulnerable packages (level *low*, warnings as errors). Image built by the .NET SDK, no Docker socket. Deploy stage guarded by an environment approval. |
| Key Vault | RBAC authorization, soft delete 90 days, purge protection. |
| Logging | Log Analytics with a 1 GB/day cap and 30-day retention; HttpClient URL logging removed. |
| Health | Separate anonymous `/healthz` for probes; the MCP endpoint is never probed. |
| Local modes | stdio and `--local` HTTP run without authentication for developer machines only: `--local` binds to `127.0.0.1` regardless of `ASPNETCORE_URLS`, a middleware rejects non-loopback remote addresses, `Ticketing:ServiceAccount` is mandatory so every write has an explicit, operator-chosen actor, and startup is refused when `CONTAINER_APP_NAME` is present. The Bicep/pipeline never set `Auth:Mode`. |

## Residual risks and options

- **Public ingress**: the endpoint is reachable from the internet but only Entra-authenticated, assigned callers can
  use it. For a stricter posture, add Container Apps IP restrictions (Foundry/Power Platform egress ranges) or move
  the app to an internal environment on a VNet and use Foundry's private MCP support.
- **Forwarded headers**: the app trusts `X-Forwarded-*` from the Container Apps ingress (the only network path to the
  container). If you ever expose the container differently, restrict `KnownNetworks`.
- **Service-account attribution**: writes made with app-only tokens are attributed to one identity. Prefer identity
  passthrough where per-user accountability matters.
- **Vendor API key scope**: the key is instance-wide; rotate it in Key Vault (`Set-TicketingApiKey.ps1`) and restart the
  revision if it is ever exposed.
- **Optional hardening**: Container Apps built-in authentication (Easy Auth) can be layered in front of the app as
  defense in depth; keep in-app validation as the primary control so MCP discovery keeps working.
