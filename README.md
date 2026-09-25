# TeamsWork Ticketing MCP server

A Model Context Protocol (MCP) server that exposes the TeamsWork **Ticketing as a Service** REST API as tools for
AI agents. It is built for three consumers:

| Consumer | Transport | Identity |
| --- | --- | --- |
| Azure AI Foundry agents | Streamable HTTP (`/mcp`) | Entra ID: project managed identity / agent identity (app role) or OAuth identity passthrough (user) |
| Copilot Studio agents | Streamable HTTP via custom connector | Entra ID OAuth 2.0 with on-behalf-of (user) |
| Local clients (Claude Code, VS Code, MCP Inspector) | stdio, or loopback-only HTTP (`--local`) | No auth; the operator must configure the account that writes are attributed to |

The remote endpoint **always** requires a Microsoft Entra ID bearer token. The unauthenticated modes bind to
stdio or `127.0.0.1` only, refuse to start without `Ticketing:ServiceAccount`, and are blocked inside Container Apps. The upstream Ticketing API key lives only in
Azure Key Vault and is injected into the container as a secret reference; it never appears in code, config files, logs,
or tool output.

## Tools

| Tool | Kind | Purpose |
| --- | --- | --- |
| `list_tickets` | read | Search / filter / sort / page tickets, with `select` to trim fields |
| `get_ticket` | read | Full ticket incl. workflow states and allowed transitions |
| `create_ticket` | write | Create a ticket (requestor defaults to the caller) |
| `update_ticket` | write | Change title, description, assignee, priority, expected date, tags, custom fields |
| `update_ticket_status` | write | Move a ticket through its workflow (resolve, close, reopen, custom states) |
| `list_ticket_activities` | read | Comment / change history, paged |
| `add_ticket_comment` | write | Add a public or private comment |
| `list_ticket_attachments` | read | Files and links on a ticket |
| `add_ticket_link_attachments` | write | Attach hyperlinks with a comment |
| `list_activity_attachments` | read | Attachments of one attachment activity |
| `get_instance` | read | Custom field definitions, assignees, workflows, SLA settings |
| `list_tag_categories` | read | Tag categories and tags |

Every write is attributed to the **authenticated caller** (from the token's claims). Tools never accept a `user`
argument, so an agent cannot impersonate someone else. App-only callers (for example a Foundry managed identity) are
attributed to the configured service account.

File uploads are intentionally not exposed (MCP tools are a poor fit for binary uploads); link attachments are.

## Repository layout

```
src/TeamsWork.Ticketing.Mcp/     .NET 10 server (both transports)
tests/                           xunit v3 tests incl. an in-process HTTP + Entra JWT integration test
infra/core.bicep                 Log Analytics, identity, ACR, Key Vault, Container Apps environment
infra/app.bicep                  The container app (scale-to-zero)
infra/scripts/                   Entra app registration + Key Vault secret helpers (PowerShell)
pipelines/azure-pipelines.yml    Azure DevOps: build, test, audit, deploy
docs/                            Setup guides: Entra, Copilot Studio, Foundry, stdio, security
```

The server was built against TeamsWork Ticketing API v1.1.0 (`https://teamswork.azure-api.net/ticketing/v1`). The
vendor's OpenAPI document is not redistributed here; obtain it from TeamsWork. If you keep a local copy in
`docs/openapi/`, it is git-ignored.

## Download a release

Pre-built binaries are on the [Releases](https://github.com/joelst/teamswork-ticketing-mcp/releases) page:
single-file executables for Windows x64, Linux x64, and macOS Apple silicon that need no .NET install, plus a
portable build that runs anywhere .NET 10 is installed (`dotnet TeamsWork.Ticketing.Mcp.dll --stdio`). The
Windows executable and the portable build's own DLL are code-signed; the Linux and macOS executables are not, and
the release notes explain the first-run prompts. Extract the archive, then follow
[Configure without the .NET SDK](#configure-without-the-net-sdk) and [Connect an MCP client](#4-connect-an-mcp-client).

### Configure without the .NET SDK

The server needs the API key and the account ticket changes are recorded under. Without the SDK, provide them in
one of two ways.

**Environment variables** (use `__` where the setting name has `:`):

| Variable | Value |
| --- | --- |
| `Ticketing__ApiKey` | Ticketing instance API key |
| `Ticketing__ServiceAccount__Id` | Your Entra object ID |
| `Ticketing__ServiceAccount__Name` | Your display name |
| `Ticketing__ServiceAccount__Email` | Your email |

Pass them from the MCP client config (for example `claude mcp add ... --env Ticketing__ServiceAccount__Id=...`).
Keep the API key out of client config files where you can, because they are stored as plain text.

**A user-secrets file**, which keeps the key out of client configs. Create this file:

- Windows: `%APPDATA%\Microsoft\UserSecrets\teamswork-taas-mcp\secrets.json`
- macOS/Linux: `~/.microsoft/usersecrets/teamswork-taas-mcp/secrets.json`

```json
{
  "Ticketing:ApiKey": "<api key>",
  "Ticketing:ServiceAccount:Id": "<your Entra object id>",
  "Ticketing:ServiceAccount:Name": "<your name>",
  "Ticketing:ServiceAccount:Email": "<your email>"
}
```

This is the same file `dotnet user-secrets set` writes, so both approaches below end up in the same place. Don't
edit the `appsettings.json` shipped next to the executable: it is read from the current directory, which MCP
clients don't set to the install folder, and every value in it already has a built-in default.

## Run it locally from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and your Ticketing instance's API key
(Ticketing app → Settings → API). Locally no Entra setup is required.

### 1. Get the code and build it

```powershell
git clone https://github.com/joelst/teamswork-ticketing-mcp.git
cd teamswork-ticketing-mcp
dotnet test                                                    # optional: 59 tests, no network needed
dotnet publish src/TeamsWork.Ticketing.Mcp -c Release -o ./publish
```

`./publish` now contains the server. On Windows run `publish\TeamsWork.Ticketing.Mcp.exe`; on macOS/Linux run
`dotnet publish/TeamsWork.Ticketing.Mcp.dll`. The folder can be moved anywhere; it needs the .NET 10 runtime.

### 2. Configure it

Two things are required: the API key, and the account that ticket changes are recorded under (there is no sign-in
locally, so you say who you are). Store them with .NET user secrets, which live in your user profile, outside the
repo and outside any MCP client config file. The published build finds them automatically.

```powershell
$p = "src/TeamsWork.Ticketing.Mcp"
dotnet user-secrets set "Ticketing:ApiKey"               "<api key>"              --project $p
dotnet user-secrets set "Ticketing:ServiceAccount:Id"    "<your Entra object id>" --project $p
dotnet user-secrets set "Ticketing:ServiceAccount:Name"  "<your name>"            --project $p
dotnet user-secrets set "Ticketing:ServiceAccount:Email" "<your email>"           --project $p
```

Your Entra object ID is shown in the Entra admin center under your user profile, or by
`az ad signed-in-user show --query id -o tsv`. Any value in the table under [Configuration](#configuration) can
instead be set as an environment variable, with `__` in place of `:` (for example `Ticketing__ApiKey`).

Optional: if your help desk is not on US Central time, set `Ticketing:DefaultTimeZoneId` (for example
`America/New_York`) the same way. The server uses it to fill in the `timezone` offset the API requires.

### 3. Check that it starts

```powershell
./publish/TeamsWork.Ticketing.Mcp.exe --local
# LOCAL MODE: no authentication. Listening on http://127.0.0.1:5188/mcp (loopback only).
# Ticket changes are attributed to <your name> <<your email>>.
```

If the API key or account is missing, it exits with a message naming the missing setting. Press Ctrl+C to stop.

### 4. Connect an MCP client

**Claude Code** (stdio; Claude Code starts and stops the server itself):

```powershell
claude mcp add --transport stdio --scope user teamswork-ticketing -- "<full path>\publish\TeamsWork.Ticketing.Mcp.exe" --stdio
```

Restart Claude Code and run `/mcp`. It should list `teamswork-ticketing` with 12 tools. Try
"list my five most recent open tickets".

**VS Code** (`.vscode/mcp.json`):

```json
{
  "servers": {
    "teamswork-ticketing": {
      "type": "stdio",
      "command": "<full path>/publish/TeamsWork.Ticketing.Mcp.exe",
      "args": ["--stdio"]
    }
  }
}
```

**Any HTTP MCP client**: start `TeamsWork.Ticketing.Mcp.exe --local` and point the client at
`http://127.0.0.1:5188/mcp` with no auth. Local mode accepts only loopback connections addressed to
`127.0.0.1`/`localhost`, so other machines and web pages cannot reach it. Change the port with `Local:Port`.

Use the published build rather than `dotnet run` for stdio clients: it starts faster and never writes build output
to stdout, which is the MCP channel.

More client examples are in [docs/stdio.md](docs/stdio.md). For the Entra-protected remote deployment see
[docs/setup-entra.md](docs/setup-entra.md), [docs/copilot-studio.md](docs/copilot-studio.md),
[docs/foundry.md](docs/foundry.md), and [docs/security.md](docs/security.md).

## Build and test

```powershell
dotnet build
dotnet test            # uses the .NET 10 Microsoft.Testing.Platform runner (see global.json)
dotnet run --project src/TeamsWork.Ticketing.Mcp -- --local    # run from source
```

## Deploy

1. Run `infra/scripts/New-EntraAppRegistrations.ps1` once (see docs/setup-entra.md).
2. Create the Azure DevOps service connection, variable group `taas-mcp-prod`, and environment `taas-mcp-prod`
   (details at the top of `pipelines/azure-pipelines.yml`).
3. Run the pipeline. After the **Core** stage has created the Key Vault, store the API key with
   `infra/scripts/Set-TicketingApiKey.ps1` (or supply it once as the secret pipeline variable `ticketingApiKey`).
4. The **Deploy** stage prints the MCP endpoint (`https://<app>.<region>.azurecontainerapps.io/mcp`).

Estimated running cost: Container Apps consumption with scale-to-zero (mostly within the free grant), ACR Basic
(~US$5/month), Key Vault and Log Analytics (cents). Under US$10/month at typical agent traffic.

## Configuration

| Key | Where | Meaning |
| --- | --- | --- |
| `Ticketing:ApiKey` | Key Vault secret `Ticketing--ApiKey` → container secret → env `Ticketing__ApiKey`; user secrets locally | Ticketing instance API key |
| `Ticketing:BaseUrl` | appsettings / env | Ticketing API base URL |
| `Ticketing:DefaultTimeZoneId` | appsettings / env | IANA zone for the API's required `timezone` offset (default `America/Chicago`) |
| `Ticketing:ServiceAccount:{Id,Name,Email}` | env / user secrets | Actor for app-only callers; **required** in stdio and `--local` modes |
| `Auth:Mode` / `--local` | CLI / env (dev only) | `Local` = unauthenticated loopback HTTP on `Local:Port` (default 5188). Unset means `Entra`; any other value stops startup |
| `Entra:TenantId`, `Entra:ClientId` | env | Server app registration (Entra HTTP mode) |
| `Entra:PublicBaseUrl` | env | Optional custom domain for protected-resource metadata |
| `KeyVault:Uri` | env (local only) | Load `Ticketing--ApiKey` from Key Vault with `DefaultAzureCredential` |
