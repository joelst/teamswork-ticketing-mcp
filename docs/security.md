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
| Authentication | Microsoft.Identity.Web validates issuer (single tenant), audience, lifetime, and signature against Entra keys. Tokens with neither `scp` nor `roles` are rejected. Startup refuses `Entra:TenantId` values of `common`, `organizations`, or `consumers`, which would accept tokens from any tenant. |
| Authorization | Policy `McpCaller`: `scp` contains `access_as_user` **or** `roles` contains `Ticketing.ReadWrite`. Service principal has *assignment required*, so unassigned users cannot even obtain a token. |
| Discovery | RFC 9728 protected-resource metadata and `WWW-Authenticate` challenge, so clients learn the authorization server without guessing; no anonymous MCP endpoint. |
| Actor integrity | Acting user comes from token claims (or the configured service account for app-only tokens). No tool accepts a `user` argument; the integration test asserts this for every tool. |
| Transport | Stateless Streamable HTTP: no server sessions, nothing to hijack, horizontal scaling safe. HTTPS-only ingress (`allowInsecure: false`); TLS terminated by Container Apps. |
| Input validation | Every tool validates arguments before they reach the upstream URL/body: UUIDs, enums, `YYYY-MM-DD` dates, datetime filters, page-size caps, text length caps, list caps (50 tags, 100 custom fields, 20 links), URL schemes (http/https only) for links. People, tags, and custom fields given by name are resolved against the instance's own lists (cached). Every person value (assignee, requestor, people-picker fields) goes through one rule: an ID or email that belongs to someone in the assignee list must match that person in full, and an assignee must be on the list, so an agent can't make a ticket look assigned to, or approved by, someone else. Custom field values are checked against each field's type and options (dates as `YYYY-MM-DD`, list options by key, people resolved), and fields of a type or option shape the server can't check are refused. Path IDs such as `activityId` are limited to letters, digits, `-` and `_`, so `..` can't navigate the upstream path. Continuation tokens must be visible ASCII, because they are sent as an HTTP header and the .NET HTTP stack writes such values to the wire unchecked (a CR/LF would inject headers into a request that carries the API key); the client re-checks every header value. |
| HTML from agents | `descriptionHtml` and `commentHtml` are sanitised with HtmlSanitizer before they are sent, keeping formatting, headings, lists, tables, quotes, code, and http(s) links. Removed: script, event handlers, frames, and non-http(s) URLs (HtmlSanitizer's defaults), plus form controls (credential-phishing forms), `style` (full-screen overlays, `url()` beacons), images and image maps (zero-click beacons that could carry data the agent read), `name` (DOM clobbering), and `target` (reverse tabnabbing). Agents can be steered by text they read, so this doesn't rely on the vendor's own sanitising. |
| Error disclosure | Only messages written for the agent (validation, upstream API errors, identity problems) reach the client. Any other exception is left to the MCP SDK, which logs it and returns a generic error, so type names, configuration, and library messages never leak. |
| Rate limiting | Per caller: 60 MCP requests a minute by default (`Mcp:RequestsPerMinutePerCaller`), keyed by the token's tenant and object ID, answered with 429 and `Retry-After`. Because one tool call can make several upstream requests, each caller is also charged for every upstream request it causes, retries included (`Ticketing:MaxUpstreamRequestsPerCallerPerMinute`, default 50 of the vendor's 100), and refused at once when its share is used, so one runaway agent can't use up the quota every caller shares. Upstream: a process-wide sliding window matching the vendor's 100 requests / 60 s, bounded retries with backoff on 429/5xx. |
| Resource limits | Tools that filter in the server (`list_my_tickets`, `list_sla_risk`, `count_tickets`) and `find_ticket_by_number` read at most `Ticketing:MaxScanTickets` tickets (default 1000) per call, and `find_similar_tickets` makes at most four requests. Scans keep only a summary of each match, so a scan of full tickets holds one page at a time. Request bodies over 1 MiB (`Mcp:MaxRequestBodyBytes`) get 413, in Kestrel and on `/mcp`. Upstream responses over 8 MiB (`Ticketing:MaxResponseBytes`) are refused, and `Ticketing:RequestTimeoutSeconds` bounds the whole upstream call including the body read (HttpClient's own timeout stops when the headers arrive). |
| Container | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`: distroless-style, non-root `app` user, no shell/package manager. The `-extra` variant adds the time zone data the server needs (and ICU, unused). Diagnostics disabled. 0.25 vCPU / 0.5 GiB. |
| Registry / pulls | ACR admin user disabled; the container app pulls with its managed identity (`AcrPull`). |
| Pipeline | Workload-identity-federation service connection (no stored cloud secrets). `NuGetAudit` fails restore on known vulnerable packages (level *low*, warnings as errors). Image built by the .NET SDK, no Docker socket. Deploy stage guarded by an environment approval. |
| Supply chain | GitHub Actions are pinned to commit SHAs, because the release workflow holds `id-token: write` and the code-signing identity and a moved tag would run new code with them. Dependabot proposes NuGet and Actions updates weekly. |
| Key Vault | RBAC authorization, soft delete 90 days, purge protection. |
| Logging | Log Analytics with a 1 GB/day cap and 30-day retention; HttpClient URL logging removed. |
| Health | Separate anonymous `/healthz` for probes; the MCP endpoint is never probed. |
| File uploads | `upload_ticket_files` exists only over stdio, and only when `Ticketing:UploadRoot` names a folder. At startup the folder's real location (links resolved, names as stored) is checked: a drive root, or a folder containing the home folder, the application data or configuration folders (where client configs can hold the API key), or the user-secrets file, is refused. Each path must resolve inside the folder, compared exactly, and each segment must name an entry exactly as stored, so a different spelling or an 8.3 short name (`ENV~1` for `.env`) finds nothing; no segment may be hidden (dot), a symbolic link or junction, or use wildcard or alternate-stream (`:`) syntax. Each file is opened, its path checked again, and read from that handle, with the size limit enforced on the bytes read (at most 10 files and `Ticketing:MaxUploadBytes`, 10 MiB). The tool is annotated open-world, since it sends local data to a third party, and uploads are private by default. An agent steered by text it has read can therefore send only files the user put in that folder. |
| Local modes | stdio and `--local` HTTP run without authentication for developer machines only: `--local` binds to `127.0.0.1` regardless of `ASPNETCORE_URLS`, a middleware rejects non-loopback remote addresses, `Ticketing:ServiceAccount` is mandatory so every write has an explicit, operator-chosen actor, and startup is refused when `CONTAINER_APP_NAME` is present. The Bicep/pipeline never set `Auth:Mode`. |

## Residual risks and options

- **Upload folder contents**: the upload checks work on names and links. A hard link placed in the upload folder, a
  FIFO or device file on Linux or macOS (reading it would block that call), or a `subst` drive letter used for the
  folder isn't detected. Each needs someone with write access to prepare the folder, which an agent can't do through
  these tools; keep the folder for files you mean to send.

- **Public ingress**: the endpoint is reachable from the internet but only Entra-authenticated, assigned callers can
  use it. For a stricter posture, add Container Apps IP restrictions (Foundry/Power Platform egress ranges) or move
  the app to an internal environment on a VNet and use Foundry's private MCP support.
- **Forwarded headers**: the app honours `X-Forwarded-For` and `X-Forwarded-Proto` from the Container Apps ingress
  (the only network path to the container), but not `X-Forwarded-Host`, which any client could set to choose the
  resource URL in the protected-resource metadata. Setting `Entra:PublicBaseUrl` pins that URL entirely. If you ever
  expose the container differently, restrict `KnownNetworks`.
- **Prompt injection**: ticket titles, descriptions, and comments are written by people, so text returned to an agent
  is untrusted input to it. The server can't prevent an agent from following instructions it reads; the controls
  above limit what a misled agent can do (no impersonation, sanitised HTML, per-caller limits).
- **Service-account attribution**: writes made with app-only tokens are attributed to one identity. Prefer identity
  passthrough where per-user accountability matters.
- **Vendor API key scope**: the key is instance-wide; rotate it in Key Vault (`Set-TicketingApiKey.ps1`) and restart the
  revision if it is ever exposed.
- **Optional hardening**: Container Apps built-in authentication (Easy Auth) can be layered in front of the app as
  defense in depth; keep in-app validation as the primary control so MCP discovery keeps working.
