<#
.SYNOPSIS
    Creates (idempotently) the Microsoft Entra app registrations for the TeamsWork Ticketing MCP server.

.DESCRIPTION
    Creates two single-tenant app registrations:

      1. Server app  (default name: taas-mcp-server)
         - Identifier URI  api://<clientId>
         - Delegated scope access_as_user (users / Copilot Studio / Foundry identity passthrough / VS Code)
         - App role        Ticketing.ReadWrite (application; for Foundry project managed identity / agent identity)
         - v2 access tokens, optional claims for email
         - Azure CLI pre-authorized so `az login` users can run the stdio transport locally
         - Service principal with "assignment required" so only assigned users/groups/identities can get tokens

      2. Copilot Studio connector app (default name: taas-mcp-copilot-connector)
         - Delegated permission to the server scope
         - Exposes access_as_user and pre-authorizes the Azure API Connections service (OBO in Copilot Studio)
         - No secret is created here; an admin creates it in the portal when configuring the connector.

    Requires Azure CLI 2.60+ and a signed-in account with Application Administrator (or Global Administrator)
    rights in the tenant. Nothing secret is written to disk or printed.

.EXAMPLE
    ./New-EntraAppRegistrations.ps1 -TenantId 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $ServerAppName = 'taas-mcp-server',
    [string] $ConnectorAppName = 'taas-mcp-copilot-connector',
    [string] $ScopeName = 'access_as_user',
    [string] $AppRoleName = 'Ticketing.ReadWrite',
    [switch] $SkipConnectorApp
)

$ErrorActionPreference = 'Stop'
$AzureCliClientId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'          # Microsoft Azure CLI
$AzureApiConnectionsClientId = 'fe053c5f-3692-4f14-aef2-ee34fc081cae' # Azure API Connections (Power Platform / Copilot Studio OBO)

function Invoke-Az {
    param([string[]] $Arguments)
    $out = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed: $out" }
    return $out
}

function Get-OrCreateApp {
    param([string] $DisplayName)
    $existing = Invoke-Az @('ad', 'app', 'list', '--display-name', $DisplayName, '--query', "[?displayName=='$DisplayName'] | [0]", '-o', 'json') | ConvertFrom-Json
    if ($existing) {
        Write-Host "Found existing app '$DisplayName' ($($existing.appId))."
        return $existing
    }
    if ($PSCmdlet.ShouldProcess($DisplayName, 'Create app registration')) {
        $created = Invoke-Az @('ad', 'app', 'create', '--display-name', $DisplayName, '--sign-in-audience', 'AzureADMyOrg', '-o', 'json') | ConvertFrom-Json
        Write-Host "Created app '$DisplayName' ($($created.appId))."
        return Invoke-Az @('ad', 'app', 'show', '--id', $created.appId, '-o', 'json') | ConvertFrom-Json
    }
}

function Ensure-ServicePrincipal {
    param([string] $AppId, [bool] $AssignmentRequired)
    $sp = Invoke-Az @('ad', 'sp', 'list', '--filter', "appId eq '$AppId'", '--query', '[0]', '-o', 'json') | ConvertFrom-Json
    if (-not $sp) {
        $sp = Invoke-Az @('ad', 'sp', 'create', '--id', $AppId, '-o', 'json') | ConvertFrom-Json
        Write-Host "Created service principal for $AppId."
    }
    if ($AssignmentRequired) {
        Invoke-Az @('ad', 'sp', 'update', '--id', $sp.id, '--set', 'appRoleAssignmentRequired=true') | Out-Null
        Write-Host "Set appRoleAssignmentRequired=true on $($sp.displayName)."
    }
    return $sp
}

function New-StableGuid {
    # Deterministic GUID from a string so re-runs keep the same scope/role IDs.
    param([string] $Seed)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Seed))
    return [Guid]::new($bytes).ToString()
}

Write-Host "Signing in context: tenant $TenantId"
Invoke-Az @('account', 'show', '--query', 'tenantId', '-o', 'tsv') | Out-Null

# --------------------------------------------------------------------------------------------------------------
# 1. Server app
# --------------------------------------------------------------------------------------------------------------
$server = Get-OrCreateApp -DisplayName $ServerAppName
$serverAppId = $server.appId
$serverObjectId = $server.id
$identifierUri = "api://$serverAppId"

$scopeId = New-StableGuid "$serverAppId/scope/$ScopeName"
$roleId = New-StableGuid "$serverAppId/role/$AppRoleName"

$serverPatch = @{
    identifierUris = @($identifierUri)
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(
            @{
                id = $scopeId
                value = $ScopeName
                type = 'User'
                isEnabled = $true
                adminConsentDisplayName = 'Access the TeamsWork Ticketing MCP server'
                adminConsentDescription = 'Allows the app to call the TeamsWork Ticketing MCP server on behalf of the signed-in user.'
                userConsentDisplayName = 'Access the ticketing assistant'
                userConsentDescription = 'Allows the app to read and update help-desk tickets on your behalf.'
            }
        )
        preAuthorizedApplications = @(
            @{ appId = $AzureCliClientId; delegatedPermissionIds = @($scopeId) }
        )
    }
    appRoles = @(
        @{
            id = $roleId
            value = $AppRoleName
            displayName = 'Ticketing read/write (application)'
            description = 'Allows an application or managed identity to read and update tickets through the MCP server. Writes are attributed to the configured service account.'
            allowedMemberTypes = @('Application')
            isEnabled = $true
        }
    )
    optionalClaims = @{
        accessToken = @(
            @{ name = 'email'; essential = $false }
            @{ name = 'preferred_username'; essential = $false }
        )
    }
}

if ($PSCmdlet.ShouldProcess($ServerAppName, 'Configure identifier URI, scope, app role, optional claims')) {
    $tmp = New-TemporaryFile
    try {
        ($serverPatch | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8
        Invoke-Az @('rest', '--method', 'PATCH', '--uri', "https://graph.microsoft.com/v1.0/applications/$serverObjectId", '--headers', 'Content-Type=application/json', '--body', "@$tmp") | Out-Null
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    Write-Host "Configured $ServerAppName ($identifierUri, scope $ScopeName, role $AppRoleName)."
}

$serverSp = Ensure-ServicePrincipal -AppId $serverAppId -AssignmentRequired $true

# --------------------------------------------------------------------------------------------------------------
# 2. Copilot Studio connector app
# --------------------------------------------------------------------------------------------------------------
$connectorAppId = $null
if (-not $SkipConnectorApp) {
    $connector = Get-OrCreateApp -DisplayName $ConnectorAppName
    $connectorAppId = $connector.appId
    $connectorScopeId = New-StableGuid "$connectorAppId/scope/access_as_user"

    $connectorPatch = @{
        identifierUris = @("api://$connectorAppId")
        api = @{
            requestedAccessTokenVersion = 2
            oauth2PermissionScopes = @(
                @{
                    id = $connectorScopeId
                    value = 'access_as_user'
                    type = 'User'
                    isEnabled = $true
                    adminConsentDisplayName = 'Allow Azure API Connections to obtain tokens on behalf of users'
                    adminConsentDescription = 'Allows the Azure API Connections service to obtain tokens on behalf of the user for the ticketing connector.'
                    userConsentDisplayName = 'Allow the ticketing connector to act on your behalf'
                    userConsentDescription = 'Allows the ticketing connector to read and update tickets as you.'
                }
            )
            preAuthorizedApplications = @(
                @{ appId = $AzureApiConnectionsClientId; delegatedPermissionIds = @($connectorScopeId) }
            )
        }
        requiredResourceAccess = @(
            @{
                resourceAppId = $serverAppId
                resourceAccess = @(
                    @{ id = $scopeId; type = 'Scope' }
                )
            }
        )
        web = @{ redirectUris = @() }
    }

    if ($PSCmdlet.ShouldProcess($ConnectorAppName, 'Configure OBO scope, pre-authorized Azure API Connections, permission to server')) {
        $tmp = New-TemporaryFile
        try {
            ($connectorPatch | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8
            Invoke-Az @('rest', '--method', 'PATCH', '--uri', "https://graph.microsoft.com/v1.0/applications/$($connector.id)", '--headers', 'Content-Type=application/json', '--body', "@$tmp") | Out-Null
        }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        Write-Host "Configured $ConnectorAppName."
    }

    Ensure-ServicePrincipal -AppId $connectorAppId -AssignmentRequired $false | Out-Null

    # Grant admin consent for the connector -> server delegated permission.
    if ($PSCmdlet.ShouldProcess($ConnectorAppName, 'Grant admin consent for server scope')) {
        try {
            Invoke-Az @('ad', 'app', 'permission', 'admin-consent', '--id', $connectorAppId) | Out-Null
            Write-Host 'Granted admin consent for the connector app.'
        }
        catch {
            Write-Warning "Admin consent could not be granted automatically ($_). Grant it in Entra admin center > App registrations > $ConnectorAppName > API permissions."
        }
    }
}

# --------------------------------------------------------------------------------------------------------------
# Summary (no secrets)
# --------------------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '================ Entra configuration summary ================'
Write-Host "Tenant ID                      : $TenantId"
Write-Host "Server app (client) ID         : $serverAppId"
Write-Host "Server identifier URI          : $identifierUri"
Write-Host "Server delegated scope         : $identifierUri/$ScopeName"
Write-Host "Server app role                : $AppRoleName (id $roleId)"
Write-Host "Server enterprise app objectId : $($serverSp.id)"
if ($connectorAppId) {
    Write-Host "Connector app (client) ID      : $connectorAppId"
}
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  * Assign users/groups who may use the MCP server: Entra admin center > Enterprise applications > $ServerAppName > Users and groups."
Write-Host "  * For Foundry managed identity / agent identity access, assign the '$AppRoleName' app role to that identity (see docs/foundry.md)."
if ($connectorAppId) {
    Write-Host "  * In Entra admin center create a client secret on '$ConnectorAppName' and enter it only in the Copilot Studio connector (see docs/copilot-studio.md)."
    Write-Host "  * After saving the connector, add the generated redirect URL to '$ConnectorAppName' > Authentication > Web."
}
Write-Host "  * Put entraTenantId=$TenantId and entraClientId=$serverAppId into the pipeline variable group / bicepparam."
