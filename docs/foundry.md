# Connect Azure AI Foundry (Foundry Agent Service)

Foundry agents call remote MCP servers over Streamable HTTP. The server is stateless, publicly reachable, and
requires an Entra ID bearer token, which matches the Foundry requirements for custom MCP servers. Two identity
models are supported; pick per agent.

| Model | Token type | Writes attributed to | Use when |
| --- | --- | --- | --- |
| **A. Project managed identity / agent identity** | app-only (`roles`) | the configured service account | All users of the agent share one identity; no per-user consent |
| **B. OAuth identity passthrough (custom OAuth)** | delegated (`scp`) | the signed-in user | Per-user attribution and permissions |

## A. Managed identity or agent identity

1. Assign the server's app role to the identity. Get the object IDs first:

   ```powershell
   $serverSp = az ad sp list --filter "appId eq '<serverClientId>'" --query "[0].id" -o tsv
   $roleId   = az ad sp show --id $serverSp --query "appRoles[?value=='Ticketing.ReadWrite'].id | [0]" -o tsv
   # <callerObjectId> = the Foundry project managed identity (or the agent identity) service principal object ID
   az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<callerObjectId>/appRoleAssignments" `
     --body "{\"principalId\":\"<callerObjectId>\",\"resourceId\":\"$serverSp\",\"appRoleId\":\"$roleId\"}"
   ```

2. Create the project connection with the server's audience:

   ```bash
   azd ai project set "https://<account>.services.ai.azure.com/api/projects/<project>"
   azd ai connection create taas-ticketing \
     --kind remote-tool \
     --target https://<app>.<region>.azurecontainerapps.io/mcp \
     --auth-type project-managed-identity \
     --audience "api://<serverClientId>"
   # or --auth-type agentic-identity for a per-agent identity
   ```

3. Make sure `Ticketing:ServiceAccount:{Id,Name,Email}` is configured on the container app (pipeline variables
   `serviceAccountId/Name/Email`). Without it, write tools return a clear error for app-only callers.

## B. OAuth identity passthrough

In the Foundry portal: Tools → Custom → **MCP** → name + endpoint → **OAuth identity passthrough** → **Custom OAuth**:

| Field | Value |
| --- | --- |
| Client ID / secret | **taas-mcp-copilot-connector** app (or a dedicated client app registered the same way) |
| Auth URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/authorize` |
| Token URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Refresh URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Scopes | `api://<serverClientId>/access_as_user offline_access` (space separated) |

Add the redirect URL Foundry shows to the client app registration (Authentication → Web). Users need at least the
**Foundry Agent Consumer** role on the project; the first call returns an `oauth_consent_request` item with a consent
link.

> Foundry blocks passing tokens for well-known Microsoft audiences to custom servers. This server uses its own
> audience (`api://<serverClientId>`), which is the supported pattern.

## Attach the tool to an agent

```python
tool = {
    "type": "mcp",
    "server_label": "ticketing",
    "server_url": "https://<app>.<region>.azurecontainerapps.io/mcp",
    "project_connection_id": "taas-ticketing",
    "allowed_tools": ["list_tickets", "get_ticket", "list_ticket_activities", "get_instance",
                      "list_tag_categories", "add_ticket_comment", "create_ticket"],
    "require_approval": {"always": ["create_ticket", "add_ticket_comment", "update_ticket", "update_ticket_status",
                                    "add_ticket_link_attachments"]},
}
```

Keep `require_approval` on the write tools unless the agent runs in a fully automated, reviewed workflow.

## Test

Prompt: *"List the three most recent urgent tickets and summarise them."* Expect a `list_tickets` call with
`priority=Urgent`, `limit=3`. Then a write prompt; confirm the approval request appears (if configured) and the
resulting activity in Teams shows the expected actor (user for B, service account for A).
