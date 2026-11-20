# Entra ID setup

The remote MCP endpoint validates Microsoft Entra ID access tokens. These app registrations are involved:

| App | Purpose | Created by |
| --- | --- | --- |
| **taas-mcp-server** | Represents the MCP server (the *resource*). Tokens must be issued for `api://<clientId>`. Exposes the delegated scope `access_as_user` and the application role `Ticketing.ReadWrite`. | `infra/scripts/New-EntraAppRegistrations.ps1` |
| **taas-mcp-copilot-connector** | The *client* Copilot Studio uses (custom connector with OAuth 2.0 / on-behalf-of). Also reusable as the "custom OAuth" client for Foundry identity passthrough. Only needed for Copilot Studio or Foundry. | same script, unless `-SkipConnectorApp` |
| **taas-mcp-cowork-client** | The *client* Microsoft 365 Copilot Cowork signs users in through. Only needed for Cowork. | `infra/scripts/Complete-CoworkSetup.ps1` (see [cowork.md](cowork.md)) |

## 1. Run the script

Requires Azure CLI 2.60+ and an account with the Application Administrator (or Global Administrator) role. Without
it, creating the first app fails with *Directory permission is needed for the current user to register the
application* (*Insufficient privileges to complete the operation*).

```powershell
az login --tenant <tenantId>
./infra/scripts/New-EntraAppRegistrations.ps1 -TenantId <tenantId>
```

Add `-SkipConnectorApp` if you don't use Copilot Studio or Foundry. The script is idempotent: run it again after
a failure and it carries on from the apps that already exist. It prints a summary (no secrets). Keep the **Server app
(client) ID**: it is the `entraClientId` pipeline variable and the `Entra:ClientId` setting;
`Deploy-TestInstance.ps1` finds it by the app's name.

What it configures on the server app:

- Identifier URI `api://<clientId>`, v2 access tokens.
- Delegated scope `access_as_user` (users and OBO flows).
- App role `Ticketing.ReadWrite` for applications (Foundry project managed identity / agent identity).
- Optional claims `email` and `preferred_username` so writes can be attributed to the user.
- Service principal with **assignment required**, so only assigned users, groups, or identities can obtain tokens.
- With `-PreAuthorizeAzureCli` only: Azure CLI pre-authorized for the scope, so `az account get-access-token` gets a
  token for the server without a consent prompt (see [section 4](#4-verify-a-token-locally)). Use it on a test tenant,
  not in production: anyone who phishes an assigned user's Azure CLI sign-in (a device-code prompt) or copies their CLI
  token cache can then call the server as that user without seeing a consent screen. The stdio transport doesn't
  need it; it uses the API key, not a token. Running the script without the switch removes an earlier
  pre-authorization, but not consent a user or admin has already granted the Azure CLI for the scope, nor tokens
  already issued (they last until they expire, about an hour). To close it fully, revoke those grants as below.

### Revoke the Azure CLI's consent for the server

The portal can revoke admin consent (Enterprise applications → Microsoft Azure CLI → Permissions), but its **User
consent** tab is read-only. Microsoft Graph lists and deletes both kinds: admin consent has `consentType`
`AllPrincipals`, and each user's has `Principal` with their object ID. It needs a Privileged Role Administrator or
Cloud Application Administrator (see Microsoft's
[Review permissions granted to enterprise applications](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/manage-application-permissions)).

```powershell
$cli    = az ad sp show --id 04b07795-8ddb-461a-bbee-02f9e1bf7b46 --query id -o tsv   # the Azure CLI
$server = az ad sp show --id <serverClientId> --query id -o tsv
# Every page: each user's consent is a grant of its own, so there can be more than Graph returns at once.
$url = "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$cli' and resourceId eq '$server'"
$grants = @()
while ($url) {
    $page = az rest --method GET --url $url | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Listing the grants failed, so none was revoked.' }
    $grants += $page.value
    $url = $page.'@odata.nextLink'
}
$grants | Format-Table id, consentType, principalId, scope
foreach ($grant in $grants) {
    az rest --method DELETE --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($grant.id)"
    if ($LASTEXITCODE -ne 0) { throw "Revoking grant $($grant.id) failed; run this again to finish." }
}
```

Tokens already issued stay valid until they expire.

## Restrict calling client applications

Entra validates who issued the token and what API audience and permission it carries. By default, the server does not
further restrict which client application obtained a delegated token. To allow only approved clients, configure the
server's `Entra:AcceptedClientApplicationIds` as a comma-separated list of the clients' application (client) IDs.
For hosted deployments, set the pipeline variable `acceptedClientApplicationIds`; for local HTTP deployments, set
the environment variable `Entra__AcceptedClientApplicationIds`. Use the client IDs for the applications that actually
request tokens (for example, the Cowork OAuth client and the Copilot Studio connector), not the server app ID.

`Entra:BlockedClientApplicationIds` is an optional comma-separated denylist; it overrides the accepted list. Both
settings accept GUIDs, are checked against the Entra v2 `azp` or v1 `appid` claim, and fail startup if any value is
not a GUID. An empty accepted list allows all otherwise-authorized clients; an empty blocked list denies none. The
hosted values are passed through `infra/app.bicep`. Configure and verify the allowlist before enforcing it, or
legitimate integrations will receive HTTP 403.

## 2. Assign who may use the server

Entra admin center → Enterprise applications → **taas-mcp-server** → Users and groups → add the users/groups who
should be able to use the ticketing tools through any agent.

> **Assign help-desk staff only.** Everyone assigned gets the whole Ticketing API key's reach, through their agent:
> every ticket and its private notes and attachments, and changing, reassigning or closing any ticket. The server has
> no per-ticket or per-role permissions yet ([design](design/role-scoped-access.md)). Don't assign a company-wide
> group, such as everyone licensed for Copilot.
>
> Keep **Assignment required** on (Enterprise applications → taas-mcp-server → Properties). The server checks only
> that a token carries the `access_as_user` scope, so with assignment off, every member and guest of the tenant can
> use it. Setting `Entra:AcceptedClientApplicationIds` (above) adds a second check on which client applications may
> call.

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

The Azure CLI needs consent for the scope first. On a test tenant, a registration made with `-PreAuthorizeAzureCli`
gives it without a prompt. Otherwise a user (where users may consent) or an admin grants it, for example by signing in
with `az login --scope api://<serverClientId>/access_as_user` (the Azure CLI has no static permission on the server, so
`/.default` can't ask for it); until then Entra refuses with *consent required*
(AADSTS65001). If `Entra:AcceptedClientApplicationIds` is set, the server also refuses the token unless the Azure CLI's
client ID, `04b07795-8ddb-461a-bbee-02f9e1bf7b46`, is in that list.

Decode it (for example at jwt.ms) and check `aud` = `<serverClientId>` (v2 tokens use the GUID), `scp` contains
`access_as_user`, and `oid`, `name`, `preferred_username` are present.
