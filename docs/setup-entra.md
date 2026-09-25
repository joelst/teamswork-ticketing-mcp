# Entra ID setup

The remote MCP endpoint validates Microsoft Entra ID access tokens. Two app registrations are involved:

| App | Purpose | Created by |
| --- | --- | --- |
| **taas-mcp-server** | Represents the MCP server (the *resource*). Tokens must be issued for `api://<clientId>`. Exposes the delegated scope `access_as_user` and the application role `Ticketing.ReadWrite`. | `infra/scripts/New-EntraAppRegistrations.ps1` |
| **taas-mcp-copilot-connector** | The *client* Copilot Studio uses (custom connector with OAuth 2.0 / on-behalf-of). Also reusable as the "custom OAuth" client for Foundry identity passthrough. | same script |

## 1. Run the script

Requires Azure CLI 2.60+ and an account with the Application Administrator (or Global Administrator) role.

```powershell
az login --tenant <tenantId>
./infra/scripts/New-EntraAppRegistrations.ps1 -TenantId <tenantId>
```

The script is idempotent and prints a summary (no secrets). Keep the **Server app (client) ID**: it is the
`entraClientId` pipeline variable and the `Entra:ClientId` setting.

What it configures on the server app:

- Identifier URI `api://<clientId>`, v2 access tokens.
- Delegated scope `access_as_user` (users and OBO flows).
- App role `Ticketing.ReadWrite` for applications (Foundry project managed identity / agent identity).
- Optional claims `email` and `preferred_username` so writes can be attributed to the user.
- Azure CLI pre-authorized for the scope, so `az login` users can run the stdio transport without a consent prompt.
- Service principal with **assignment required**, so only assigned users, groups, or identities can obtain tokens.

## 2. Assign who may use the server

Entra admin center → Enterprise applications → **taas-mcp-server** → Users and groups → add the users/groups who
should be able to use the ticketing tools through any agent.

For application callers (Foundry managed identity or agent identity) assign the app role instead; see
[foundry.md](foundry.md).

## 3. Copilot Studio connector secret

The script does not create a client secret. When you configure the connector (see
[copilot-studio.md](copilot-studio.md)), create a secret on **taas-mcp-copilot-connector** in the Entra admin center
and paste it only into the connector's security settings. After saving the connector, add its generated redirect
URL to the app registration under Authentication → Web.

## 4. Verify a token locally

```powershell
az account get-access-token --resource api://<serverClientId> --query accessToken -o tsv
```

Decode it (for example at jwt.ms) and check `aud` = `<serverClientId>` (v2 tokens use the GUID), `scp` contains
`access_as_user`, and `oid`, `name`, `preferred_username` are present.
