# Copilot Cowork plugin

Microsoft 365 Copilot Cowork uses the hosted MCP endpoint through a plugin: a Microsoft 365 app package holding a
connector to `/mcp` and a few skills. The server itself doesn't change. It's the same endpoint Copilot Studio and
Foundry use, and every write is attributed to the Cowork user signed in through it.

| Piece | Where |
| --- | --- |
| Skills (the same `SKILL.md` files work in Cowork and Claude Code) | `plugin/skills/` |
| Cowork manifest template and icons | `plugin/cowork/` |
| Claude Code plugin manifest (skills only) | `plugin/.claude-plugin/plugin.json` |
| Package builder and checks | `scripts/Build-CoworkPackage.ps1`, `scripts/tests/Test-CoworkPlugin.ps1` |
| Setup helper (Entra client, consent, users, build, install) | `infra/scripts/Complete-CoworkSetup.ps1` |
| Tool description (a copy of `tools/list`) | `plugin/cowork/tools/`, refreshed by `scripts/Update-CoworkToolDescription.ps1` |

The skills:

- **ticket-triage** reads new or unassigned tickets, checks for duplicates, and proposes a priority, assignee and
  tags for you to approve.
- **my-ticket-queue** is read-only. It lists your tickets and SLA risks, and suggests what to do next.
- **ticket-update** handles replies, notes, status changes, assignment, field changes, links and new tickets. It
  confirms each change before making it.

## Before you start

In this order:

1. **The server's app registration**, from `./infra/scripts/New-EntraAppRegistrations.ps1 -TenantId <tenantId>
   -SkipConnectorApp`. Leave out `-SkipConnectorApp` if you also use Copilot Studio. See
   [setup-entra.md](setup-entra.md). Your account needs the Application Administrator role; without it the script
   stops with *Insufficient privileges to complete the operation*.
2. **A deployed endpoint**, such as `https://<app>.<region>.azurecontainerapps.io/mcp`. See
   [Deploy](../README.md#deploy). For a test instance deployed with your own sign-in, run
   `./infra/scripts/Deploy-TestInstance.ps1 -Location <region>`. It finds the app registration from step 1 by name, so
   run that first.
3. **The people who may use it**, assigned to the server's enterprise app, which requires assignment: an unassigned
   user's sign-in is refused. `Complete-CoworkSetup.ps1 -AssignUser <UPN>` or `-AssignGroup <group>` does this.
   Assigning a group needs Entra ID P1 or P2.
4. **Node.js**, for the Microsoft 365 Agents Toolkit CLI (`atk`), which validates and installs the package. The setup
   helper runs it through `npx` if it isn't installed; to install it yourself:
   `npm install -g @microsoft/m365agentstoolkit-cli` (1.1.12 or later).

## The quick route

`infra/scripts/Complete-CoworkSetup.ps1` does steps 1, 3 and 4 below, as well as assigning users. It prints step 2's
values ready to paste; step 2 is the one part done by hand, since the developer portal has no documented API for it. Run it in two passes, signed in with `az login` as an Application Administrator:

```powershell
# Pass 1: the Entra client, consent, users, and a secret copied to the clipboard (never printed)
./infra/scripts/Complete-CoworkSetup.ps1 -ResourceGroup rg-taasmcp-test -AssignUser <your UPN> -CreateSecret -OpenPortal

# Create the developer portal registration from the printed values, then pass 2: build, validate and install
./infra/scripts/Complete-CoworkSetup.ps1 -ResourceGroup rg-taasmcp-test -OAuthReferenceId '<OAuth client registration ID>' `
    -DeveloperName '<your organization>' -WebsiteUrl 'https://<your site>' `
    -PrivacyUrl 'https://<your site>/privacy' -TermsUrl 'https://<your site>/terms' -Install
```

Pass 1 checks that the endpoint refuses a request without a token (401), and fills the endpoint in as the developer
portal's Base URL. Without a deployment it still does the Entra steps and warns that the endpoint is unknown; run it
again with `-ResourceGroup` once the app is deployed, before filling in the portal. The secret stays on the clipboard
only until you copy something else, so add `-CreateSecret` on the run just before you fill in the portal.

It's safe to run again: it finds what exists and updates it. `-WhatIf` shows the Entra changes without making them.
Without `-CreateSecret`, create the secret yourself as in step 1. Then do step 5 (connect and turn it on). The steps
below describe what the script does, for doing them by hand.

## 1. Register an OAuth client in Entra

Cowork signs users in to the server through an OAuth client you register. Connectors have no Microsoft Entra SSO
option, and Entra doesn't support dynamic client registration.

In the Entra admin center, go to **App registrations** → **New registration**, and name it `taas-mcp-cowork-client`
with single-tenant accounts. Then:

- **Authentication → Add a platform → Web**, with the redirect URI
  `https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect`. Then create a client secret under **Certificates &
  secrets**. Enter it only in the developer portal in step 2; never put it in a file or in this repository. Note its
  expiry date, because sign-ins stop working when it expires.

  Microsoft's guide also describes a public client with no secret: a **Single-page application** platform, with PKCE
  securing the code exchange. That hasn't been tried with Entra here. Entra may refuse a single-page application's
  code when Teams exchanges it server-side rather than from a browser, and it limits such an app's refresh tokens to
  24 hours. Use Web unless you've confirmed the public client works.
- **API permissions**: add the server app's delegated `access_as_user` permission, and `offline_access`. Then
  **Grant admin consent**. Consent isn't checked when the auth config is created, so without it every sign-in fails
  later with *Need admin approval*.

## 2. Create the auth config in the Teams developer portal

In the [Teams developer portal](https://dev.teams.microsoft.com/tools), go to **Tools** → **OAuth client
registration** → **Register client**:

| Field | Value |
| --- | --- |
| Base URL | The endpoint, such as `https://<app>.<region>.azurecontainerapps.io/mcp` |
| Restrict usage by org | **My organization only** |
| Restrict usage by app | **Any Teams app**. A registration bound to one app ID makes every tool call return 404. |
| Client ID | The client ID of `taas-mcp-cowork-client` |
| Client secret | The secret from step 1 |
| Authorization endpoint | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/authorize` |
| Token endpoint | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Refresh endpoint | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Scope | `api://<serverClientId>/access_as_user offline_access` |
| PKCE | On |

Saving it gives an **OAuth client registration ID**, which the build uses as the connector's `referenceId`. The ID
isn't a secret; it only points at the registration.

## 3. Build the package

```powershell
./scripts/Build-CoworkPackage.ps1 `
    -McpServerUrl 'https://<app>.<region>.azurecontainerapps.io/mcp' `
    -OAuthReferenceId '<OAuth client registration ID>' `
    -DeveloperName '<your organization>' -WebsiteUrl 'https://<your site>' `
    -PrivacyUrl 'https://<your site>/privacy' -TermsUrl 'https://<your site>/terms'
```

The script checks the skills and manifest against Microsoft's upload rules first. These include:

- each skill's `name` matches its folder and is in kebab-case;
- each skill has a description;
- there are no more than 20 skills;
- the manifest uses no fields outside the v1.28 schema.

It then writes `artifacts/cowork/teamswork-ticketing-cowork.zip`. `./scripts/Build-CoworkPackage.ps1 -CheckOnly`
runs just the checks; CI runs them with `scripts/tests/Test-CoworkPlugin.ps1`.

Before uploading, check the package with the upload's own rules:
`atk validate --package-file ./artifacts/cowork/teamswork-ticketing-cowork.zip`. It reports one warning, which you can
ignore for your own organization: the short name contains "Teams", from the vendor's product name TeamsWork, and
Store listings shouldn't use Microsoft product names.

The package also carries `tools/teamswork-ticketing-tools.json`, a copy of the endpoint's `tools/list`. The v1.28
schema requires it, though Cowork reads the tools from the endpoint itself. After a change to the server's tools,
refresh it with `./scripts/Update-CoworkToolDescription.ps1`; the tests fail when its tool names fall out of step with
the source.

The icons in `plugin/cowork/` are placeholders. Replace them before publishing: `color.png` is 192×192, and
`outline.png` is a 32×32 white outline on a transparent background.

## 4. Install it for yourself

```powershell
atk auth login
atk install --file-path ./artifacts/cowork/teamswork-ticketing-cowork.zip --scope Personal
```

Keep the `TitleId` and `AppId` it prints, for updating or removing it later.

## 5. Connect it and turn it on

Open **Cowork** → **Sources & Skills** → **Plugins** → **TeamsWork Ticketing**. Connect its connector, which is
where you sign in with your work account, and then **turn it on**. It stays off after installing and connecting,
and while it's off Cowork has none of its tools: the skills load, but answer that the ticketing connection isn't
available. Plugins load when a conversation starts, so start a new one and try:

- "Who am I in the ticketing system?"
- "What's on my plate?"
- "Triage today's new tickets"
- "Add a private note to ticket 1234 saying the vendor has been called"

Changes are recorded as you: `whoami` shows the account. The server accepts any client with a token for its
`access_as_user` scope, so the new client needs no server setting.

## 6. Publish to the organization

Go to Microsoft 365 admin center → **Manage apps** → **Upload custom app** → **...** → **Add agent**, and upload the
same `.zip`. For an update, raise `version` in `plugin/cowork/manifest.json`, rebuild, and upload again. Keep `id`
unchanged: it identifies the plugin across versions.

## Troubleshooting

| What you see | Likely cause |
| --- | --- |
| The skill answers that the ticketing connection isn't available | The connector is installed but off, or not connected: see step 5, then start a new conversation. |
| Sign-in fails with *Need admin approval* | Admin consent for `taas-mcp-cowork-client` is missing. Run `Complete-CoworkSetup.ps1` again, or grant it in Entra admin center → App registrations → `taas-mcp-cowork-client` → API permissions. |
| Sign-in is refused for one person | They aren't assigned to the server's enterprise app: `Complete-CoworkSetup.ps1 -AssignUser <UPN>`. |
| Every tool call returns 404 | The developer portal registration is restricted to one app ID; set **Restrict usage by app** to **Any Teams app**. |
| Tools are missing, or sign-in never completes | The portal registration's Base URL doesn't match the endpoint exactly (including `/mcp`), or the client secret has expired. |
| Sign-ins stop working after months | The client secret expired. `Complete-CoworkSetup.ps1` warns 30 days ahead; run it with `-CreateSecret` and paste the new secret into the portal registration. |

To see whether Cowork reaches the server at all, look at the container app's request count by status code in the
Azure portal (the container app → **Metrics** → **Requests**, split by status code category). A 2xx at the moment
of a Cowork question means Cowork called the server with a valid token; a 4xx means it was refused; nothing means it
never called. The server doesn't log requests by default. For a short test you can set
`Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information` on a test app to log each request, and
remove it afterwards: the platform's health check every 10 seconds is logged too.

## Using the skills in Claude Code

The skills also work in Claude Code with the local server that `scripts/install.ps1` or `install.sh` registers. To
make them available in every project, copy the folders in `plugin/skills/` to `~/.claude/skills/`; for one project,
copy them to its `.claude/skills/`. The Claude plugin has no MCP configuration of its own, because the installer
already registers the server.

## Limits

- **File uploads:** `upload_ticket_files` is offered only over stdio, so Cowork can attach links but not files.
  Taking files from Cowork's workspace would need a new server tool with a `contentEncoding: base64` parameter.
- **Tool call time:** Cowork expects each tool call to finish within 30 seconds. `list_my_tickets`, `list_sla_risk`,
  `count_tickets` and `find_ticket_by_number` read up to `Ticketing:MaxScanTickets` tickets (default 1000), so time
  them on your instance.
- **Confirmation prompts:** every tool declares MCP safety annotations. Cowork's confirmation prompts for
  non-Microsoft servers are still rolling out, which is why the skills themselves ask before each change.
- **Mobile:** custom plugins don't run in Cowork on mobile.
- **Network:** Cowork calls the endpoint from Microsoft's cloud, not from your users' networks, so office IP
  addresses don't help restrict it. See [security.md](security.md#residual-risks-and-options) for what does.

References: [Build plugins for Copilot Cowork](https://learn.microsoft.com/en-us/microsoft-365/copilot/cowork/cowork-plugin-development),
[Register MCP servers as agent connectors](https://learn.microsoft.com/en-us/microsoftteams/platform/m365-apps/agent-connectors),
[Configure OAuth 2.0 authentication](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/plugin-authentication-oauth).
