# Connect Copilot Studio

Copilot Studio reaches MCP servers through the Power Platform connector framework. The connector authenticates
users with Entra ID and calls the MCP server **on behalf of the signed-in user**, so every ticket change is
attributed to that person.

Prerequisites: the server is deployed (you have the `https://<app>.<region>.azurecontainerapps.io/mcp` endpoint) and
[setup-entra.md](setup-entra.md) has been completed.

## 1. Create the connector

In Copilot Studio open your agent → **Tools** → **Add a tool** → **New tool** → **Model Context Protocol**.

| Field | Value |
| --- | --- |
| Server name | TeamsWork Ticketing |
| Server URL | `https://<app>.<region>.azurecontainerapps.io/mcp` |
| Authentication | **OAuth 2.0** → **Manual** (or *Microsoft Entra ID* identity provider where offered) |
| Client ID | Application (client) ID of **taas-mcp-copilot-connector** |
| Client secret | Secret created on that app registration |
| Authorization URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/authorize` |
| Token URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Refresh URL | `https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token` |
| Scope | `api://<serverClientId>/access_as_user offline_access` |

If the wizard offers the **Microsoft Entra ID** identity provider with an **Enable on-behalf-of login** switch (the
custom connector "Security" tab), use: Resource URL `api://<serverClientId>`, scope
`api://<serverClientId>/access_as_user`, OBO login = true. This is the seamless-SSO variant described in Microsoft's
[OBO for custom connectors](https://learn.microsoft.com/microsoft-copilot-studio/advanced-custom-connector-on-behalf-of)
guide, which the app registration script already prepared (Azure API Connections pre-authorized).

## 2. Add the redirect URL

After saving, Copilot Studio shows a **Redirect URL**. Add it to **taas-mcp-copilot-connector** → Authentication →
Web platform → Redirect URIs.

## 3. Connect and pick tools

Create the connection when prompted (a user consent dialog appears the first time). Select the tools the agent
should have. Recommended for a helpdesk agent: all read tools plus `add_ticket_comment` and `create_ticket`; add
`update_ticket` and `update_ticket_status` only for agent-assist scenarios where a human reviews the action.

## 4. Test

Ask the agent: *"Show my three most recent open tickets"* → `list_tickets`. Then *"Add a comment to ticket 1839
saying the technician is on the way"* → the agent calls `list_tickets` (to resolve the number to a UUID) and
`add_ticket_comment`. Open the ticket in Teams and confirm the comment is attributed to the signed-in user.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| 401 when the connector calls the server | Token audience is not the server app. Check Resource URL / scope use the **server** client ID, not the connector's. |
| 403 | Token lacks `access_as_user`. Grant admin consent on the connector app's API permission. |
| "Neither scope or roles claim was found" | Same as above. |
| Consent loop / AADSTS65001 | Admin consent missing for connector → server permission. Run the script again or grant consent in the portal. |
| Writes attributed to the service account | Token is app-only (client credentials). Use OAuth with user sign-in, not client credentials. |
