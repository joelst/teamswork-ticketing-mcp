# Run locally (stdio and loopback HTTP)

Locally you do not need Entra ID. Two unauthenticated modes exist, and both **require** you to say who ticket
changes are attributed to, because there is no token to read an identity from:

| Mode | Start with | Listens on | Auth |
| --- | --- | --- | --- |
| stdio | `--stdio` (or `MCP_TRANSPORT=stdio`) | stdin/stdout | none |
| local HTTP | `--local` (or `Auth:Mode=Local`) | `http://127.0.0.1:5188/mcp` only (`Local:Port` to change) | none; non-loopback connections get 403 |

Both modes refuse to start unless `Ticketing:ServiceAccount:Id`, `:Name`, and `:Email` are set. Use your own
Entra object ID, name, and email so ticket history shows you as the author. Local mode also refuses to start inside
Azure Container Apps, and the remote deployment never sets `Auth:Mode`, so the Entra requirement cannot be switched
off by accident in the cloud.

## API key

Sources, in order: environment `Ticketing__ApiKey`, .NET user secrets, or `KeyVault:Uri` (loads secret
`Ticketing--ApiKey` with `DefaultAzureCredential`; needs *Key Vault Secrets User* on the vault). User secrets are
loaded in every environment for the stdio and local modes, so this is the easiest option:

```powershell
dotnet user-secrets set "Ticketing:ApiKey" "<api key>" --project src/TeamsWork.Ticketing.Mcp
```

The service-account values can go in user secrets too (`Ticketing:ServiceAccount:Id` and so on) instead of the
environment.

## Claude Code

Build once, then register the built DLL (faster startup than `dotnet run`):

```powershell
dotnet build <repo> -c Release

claude mcp add --transport stdio --scope user teamswork-ticketing `
  --env Ticketing__ServiceAccount__Id=<your Entra object id> `
  --env Ticketing__ServiceAccount__Name="<your name>" `
  --env Ticketing__ServiceAccount__Email=<your email> `
  -- dotnet <repo>\src\TeamsWork.Ticketing.Mcp\bin\Release\net10.0\TeamsWork.Ticketing.Mcp.dll --stdio
```

Or as a project `.mcp.json`:

```json
{
  "mcpServers": {
    "teamswork-ticketing": {
      "command": "dotnet",
      "args": ["<repo>/src/TeamsWork.Ticketing.Mcp/bin/Release/net10.0/TeamsWork.Ticketing.Mcp.dll", "--stdio"],
      "env": {
        "Ticketing__ServiceAccount__Id": "<your Entra object id>",
        "Ticketing__ServiceAccount__Name": "<your name>",
        "Ticketing__ServiceAccount__Email": "<your email>"
      }
    }
  }
}
```

Restart Claude Code and run `/mcp`; the server should list 12 tools.

## VS Code (`.vscode/mcp.json`)

```json
{
  "servers": {
    "teamswork-ticketing": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/src/TeamsWork.Ticketing.Mcp", "--", "--stdio"],
      "env": {
        "Ticketing__ServiceAccount__Id": "<your Entra object id>",
        "Ticketing__ServiceAccount__Name": "<your name>",
        "Ticketing__ServiceAccount__Email": "<your email>"
      }
    }
  }
}
```

## Local HTTP

```powershell
dotnet run --project src/TeamsWork.Ticketing.Mcp -- --local
# LOCAL MODE: no authentication. Listening on http://127.0.0.1:5188/mcp (loopback only). Ticket changes are attributed to ...

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5188/mcp `
  -Headers @{ Accept = "application/json, text/event-stream" } -ContentType application/json `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

Point any HTTP MCP client (VS Code `"type": "http"`, MCP Inspector, Claude Code `--transport http`) at that URL with
no headers.

## HTTP with Entra locally

To exercise the real auth pipeline, omit `--local` and set `Entra__TenantId` and `Entra__ClientId`:

```powershell
$token = az account get-access-token --resource api://<serverClientId> --query accessToken -o tsv
Invoke-RestMethod -Method Post -Uri http://localhost:5000/mcp `
  -Headers @{ Authorization = "Bearer $token"; Accept = "application/json, text/event-stream" } `
  -ContentType application/json `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

Unauthenticated requests receive `401` with a `WWW-Authenticate: Bearer resource_metadata="…"` challenge, and
`GET /.well-known/oauth-protected-resource` describes the Entra authorization server and scope, so spec-compliant
clients (VS Code, Claude) can discover how to sign in. Against the deployed server, VS Code needs only the URL:

```json
{ "servers": { "teamswork-ticketing-remote": { "type": "http", "url": "https://<app>.<region>.azurecontainerapps.io/mcp" } } }
```

## MCP Inspector

```powershell
npx @modelcontextprotocol/inspector dotnet run --project src/TeamsWork.Ticketing.Mcp -- --stdio
```
