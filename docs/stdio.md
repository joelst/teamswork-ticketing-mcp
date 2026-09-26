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

MCP clients start stdio servers themselves, so for everyday use you install the executable once and point each
client at it.

## Install with the script

The install script is the quickest way to set up one machine. It:

1. Downloads the executable for your platform from the newest release that has one (pre-releases included), and
   checks it against the release's `SHA256SUMS.txt`. The checksum file comes from the same release, so it only
   proves the download is intact. On Windows the script also requires a valid Authenticode signature, which proves
   the file was signed with a trusted code-signing certificate and not changed since, and prints the signer.
2. Puts the executable at a fixed per-user path, so client configurations keep working across upgrades:
   - Windows: `%LOCALAPPDATA%\Programs\teamswork-ticketing-mcp\TeamsWork.Ticketing.Mcp.exe`
   - macOS/Linux: `~/.local/share/teamswork-ticketing-mcp/TeamsWork.Ticketing.Mcp`
3. Asks for the Ticketing API key (hidden input) and the account that ticket changes are attributed to, and saves
   them to the [user-secrets file](#settings). If the Azure CLI is signed in, your Entra object ID, name, and email are
   offered as defaults. Press Enter at any prompt to keep the current value.
4. Starts the server once over stdio to check that it comes up.
5. Registers it as `teamswork-ticketing` with every supported client it finds on `PATH`: Claude Code (`claude`),
   Codex CLI (`codex`), GitHub Copilot CLI (`copilot`), and VS Code / GitHub Copilot Chat (`code`). Visual Studio
   has no command line for this; see [Visual Studio](#github-copilot-in-visual-studio). Under WSL, clients that
   are Windows programs (such as `code`) are skipped: run `install.ps1` on Windows for those.

No client configuration contains the API key: clients get only the executable path and `--stdio`.

**Windows** (PowerShell 7 or Windows PowerShell 5.1):

```powershell
irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1 | iex
```

**macOS / Linux**:

```sh
curl -fsSL https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.sh | sh
```

Both scripts are also attached to each [release](https://github.com/joelst/teamswork-ticketing-mcp/releases); a copy
downloaded from a release installs that release rather than the newest. Read a script before piping it to a shell if
you haven't seen it before.

The scripts call the GitHub API, which allows 60 unauthenticated requests an hour per IP address. If a shared network
hits that limit, set `GITHUB_TOKEN` to any GitHub token and the scripts will use it.

### Options

| PowerShell | sh | Meaning |
| --- | --- | --- |
| `-Clients claude,vscode` | `--clients claude,vscode` | Register only with these: `claude`, `codex`, `copilot`, `vscode`, `all`, or `none`. Default: every client found on `PATH` |
| `-Version v0.2.0` | `--version v0.2.0` | Install a specific release, or `latest` for the newest |
| `-InstallDir <dir>` | `--install-dir <dir>` | Install somewhere else |
| `-SkipSecrets` | `--skip-secrets` | Don't prompt; keep the secrets file as it is (or use environment variables) |
| `-Uninstall` | `--uninstall` | Unregister from the clients and delete the server's files (and the folder, if it is then empty) |
| `-RemoveSecrets` | `--remove-secrets` | With uninstall, also delete the secrets file |

To pass options to the one-liner:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1))) -Clients claude,copilot
```

```sh
curl -fsSL https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.sh | sh -s -- --clients claude,copilot
```

### Upgrade and uninstall

Run the same command again to upgrade. The new executable replaces the old one at the same path, so the clients'
registrations stay valid, and you don't need to close the clients first: ones that are running keep the old version
until they restart. (On Windows the old file is renamed aside, because a running executable can't be overwritten,
and deleted on a later run.)

To uninstall, run the script with `-Uninstall` / `--uninstall`. It deletes only the files it installed (the
executable and a `.version` file beside it), so an `-InstallDir` shared with other programs is safe. Quit the MCP
clients first on Windows, where a running executable can't be deleted. VS Code has no command to remove a server:
run **MCP: Open User Configuration** and delete the `teamswork-ticketing` entry.

## Settings

The server needs the Ticketing API key and the account that ticket changes are attributed to. It reads them from
these sources in turn, and **a later source overrides an earlier one** for any setting both contain:

1. Environment variables, with `__` in place of `:`: `Ticketing__ApiKey`, `Ticketing__ServiceAccount__Id`,
   `Ticketing__ServiceAccount__Name`, `Ticketing__ServiceAccount__Email`.
2. The .NET user-secrets file, which the install script writes and `dotnet user-secrets` edits. A value here wins
   over the same environment variable, including one set in a client config:
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

3. `KeyVault:Uri` (set in either of the above), which loads secret `Ticketing--ApiKey` with `DefaultAzureCredential`
   (needs *Key Vault Secrets User* on the vault). It overrides the API key from both.

Prefer the secrets file. Client configurations are plain text and are often synced or shared, so keep the API key
out of them; the manual examples below pass no settings for that reason. If you do use environment variables in a
client config, put only the service-account values there, and leave those keys out of the secrets file, since its
values would win.

Optional: `Ticketing:DefaultTimeZoneId` (for example `America/New_York`) if your help desk is not on US Central time.
Add it to the secrets file; the install script keeps it when you run it again.

`install.sh` updates the file without a JSON parser, so it only edits the form `dotnet user-secrets` writes: one
`"key": "value"` setting per line. It stops, without changing anything, if the file looks different, and keeps the
previous version as `secrets.json.bak`.

## Connect a client manually

Use these if you installed without the script, built from source, or want to see what the script did. In each
example, replace `<exe>` with the full path to the executable:

| Installed with | Windows | macOS / Linux |
| --- | --- | --- |
| Install script | `C:\Users\<you>\AppData\Local\Programs\teamswork-ticketing-mcp\TeamsWork.Ticketing.Mcp.exe` | `/Users/<you>/.local/share/teamswork-ticketing-mcp/TeamsWork.Ticketing.Mcp` (macOS), `/home/<you>/…` (Linux) |
| Release archive | `<folder>\TeamsWork.Ticketing.Mcp.exe` | `<folder>/TeamsWork.Ticketing.Mcp` |
| From source (`dotnet publish src/TeamsWork.Ticketing.Mcp -c Release -o publish`) | `<repo>\publish\TeamsWork.Ticketing.Mcp.exe` | `<repo>/publish/TeamsWork.Ticketing.Mcp` (needs the .NET 10 runtime) |

Write the path out in full: not every client expands `~` or environment variables in the command. For the portable
build, the command is `dotnet` and the arguments are `<folder>/TeamsWork.Ticketing.Mcp.dll --stdio`.

Every client should then list `teamswork-ticketing` with 12 tools. Try "list my five most recent open tickets".

### Claude Code

```sh
claude mcp add --transport stdio --scope user teamswork-ticketing -- "<exe>" --stdio
```

`--scope user` makes it available in every project. Check it with `claude mcp get teamswork-ticketing` (it should say
**Connected**) or `/mcp` inside Claude Code. To share it with a repository instead, use `--scope project`, which
writes `.mcp.json`:

```json
{
  "mcpServers": {
    "teamswork-ticketing": {
      "command": "<exe>",
      "args": ["--stdio"]
    }
  }
}
```

### Codex CLI

```sh
codex mcp add teamswork-ticketing -- "<exe>" --stdio
```

This writes `~/.codex/config.toml`, which you can also edit directly. Use a single-quoted TOML string for Windows paths
so the backslashes are kept:

```toml
[mcp_servers.teamswork-ticketing]
command = 'C:\Users\<you>\AppData\Local\Programs\teamswork-ticketing-mcp\TeamsWork.Ticketing.Mcp.exe'
args = ["--stdio"]
```

Check it with `codex mcp list`, or `/mcp` inside Codex. The Codex IDE extension reads the same file.

### GitHub Copilot in VS Code

```sh
code --add-mcp '{"name":"teamswork-ticketing","type":"stdio","command":"<exe>","args":["--stdio"]}'
```

In PowerShell on Windows, escape the quotes for `code.cmd`, and double the backslashes because the path is inside
JSON:

```powershell
code --add-mcp '{\"name\":\"teamswork-ticketing\",\"type\":\"stdio\",\"command\":\"C:\\Users\\<you>\\AppData\\Local\\Programs\\teamswork-ticketing-mcp\\TeamsWork.Ticketing.Mcp.exe\",\"args\":[\"--stdio\"]}'
```

This adds the server to your user profile's `mcp.json` (open it with **MCP: Open User Configuration**), so it is
available in every workspace:

```json
{
  "servers": {
    "teamswork-ticketing": {
      "type": "stdio",
      "command": "<exe>",
      "args": ["--stdio"]
    }
  }
}
```

For one repository only, put the same `servers` block in `.vscode/mcp.json`. Then open Copilot Chat, switch to
**Agent** mode, and check that the `teamswork-ticketing` tools are selected in the tools picker. **MCP: List Servers**
shows the server's state and output.

### GitHub Copilot CLI

```sh
copilot mcp add teamswork-ticketing -- "<exe>" --stdio
```

This writes `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "teamswork-ticketing": {
      "type": "local",
      "command": "<exe>",
      "args": ["--stdio"],
      "tools": ["*"]
    }
  }
}
```

Check it with `copilot mcp get teamswork-ticketing`, or `/mcp` inside Copilot CLI.

### GitHub Copilot in Visual Studio

Visual Studio 2022 17.14 or later reads `%USERPROFILE%\.mcp.json` for every solution. It also reads a solution's
`.mcp.json` and `.vscode\mcp.json`. Create or edit the file:

```json
{
  "servers": {
    "teamswork-ticketing": {
      "type": "stdio",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\teamswork-ticketing-mcp\\TeamsWork.Ticketing.Mcp.exe",
      "args": ["--stdio"]
    }
  }
}
```

In Copilot Chat, switch to **Agent** mode and enable the tools in the tools picker; Visual Studio adds new MCP tools
turned off.

### Copilot coding agent and other cloud agents

The Copilot coding agent, and any agent that runs in the cloud, can't start an executable on your machine. Give those
the [Entra-protected remote deployment](setup-entra.md) instead.

If your organization uses Copilot Business or Enterprise, an administrator may need to allow MCP servers, or add this
one to the organization's MCP allow list, before any Copilot client will use it.

## Troubleshooting

**The client lists the server but it fails to start.** Run `<exe> --stdio` in a terminal. If a setting is missing
or wrong, it prints what to fix and where the secrets file is, then exits. If it prints who ticket changes will be
attributed to and keeps running, it started; press Ctrl+C. The startup check doesn't call the Ticketing API, so a
wrong API key only shows up when a tool is used.

**Linux: `Couldn't find a valid ICU package`.** The Linux executable needs ICU. Install it with your package
manager, for example `sudo apt install libicu-dev` on Debian/Ubuntu (or the versioned `libicuNN` package) or
`sudo dnf install libicu` on Fedora. Minimal images, including some WSL distributions, don't include it.

**Windows: SmartScreen or "blocked" warnings.** The Windows executable is code-signed, and the install script
unblocks it. For a manually downloaded file, run `Unblock-File <exe>`.

**Windows: uninstall stops with "still running".** A client is running the server. Quit Claude Code, Codex, Copilot
CLI, VS Code, and Visual Studio (or stop the `teamswork-ticketing` server from the client), then run it again.

**`install.sh` stops because it can't update the secrets file safely.** The file isn't in the one-setting-per-line
form. Edit it by hand to match the example under [Settings](#settings), or run the script with `--skip-secrets`.

**Windows: `irm … | iex` is blocked.** Some managed devices run PowerShell in Constrained Language mode, which stops
the script. Download the release zip instead, extract the executable to the path above, write the secrets file by
hand, and connect the clients manually.

## Run from source

For development, point a client at the build output rather than a release:

```powershell
dotnet user-secrets set "Ticketing:ApiKey" "<api key>" --project src/TeamsWork.Ticketing.Mcp
dotnet user-secrets set "Ticketing:ServiceAccount:Id" "<your Entra object id>" --project src/TeamsWork.Ticketing.Mcp
dotnet user-secrets set "Ticketing:ServiceAccount:Name" "<your name>" --project src/TeamsWork.Ticketing.Mcp
dotnet user-secrets set "Ticketing:ServiceAccount:Email" "<your email>" --project src/TeamsWork.Ticketing.Mcp
dotnet build -c Release

claude mcp add --transport stdio --scope user teamswork-ticketing-dev -- `
  dotnet <repo>\src\TeamsWork.Ticketing.Mcp\bin\Release\net10.0\TeamsWork.Ticketing.Mcp.dll --stdio
```

These write the same secrets file the install script uses. Register the built DLL rather than `dotnet run`: it starts
faster and never writes build output to stdout, which is the MCP channel. In VS Code you can run straight from the
workspace in `.vscode/mcp.json`:

```json
{
  "servers": {
    "teamswork-ticketing-dev": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/src/TeamsWork.Ticketing.Mcp", "--", "--stdio"]
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
