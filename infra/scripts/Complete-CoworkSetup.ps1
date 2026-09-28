<#
.SYNOPSIS
    Completes the Copilot Cowork setup (docs/cowork.md): the Entra OAuth client, consent, user assignment, the Teams
    developer portal values, and the package build and install.

.DESCRIPTION
    Idempotent; run it again with more parameters as each step's inputs become available.

      1. Finds the MCP endpoint: -McpServerUrl, or the container app in -ResourceGroup.
      2. Creates or updates the OAuth client app (default name: taas-mcp-cowork-client):
         - a Web platform redirect URI to https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect;
         - delegated permissions to the server's access_as_user scope and Microsoft Graph offline_access;
         - admin consent for both.
      3. With -CreateSecret, adds a client secret and puts it on the clipboard only. It is never printed or written to
         a file; paste it into the developer portal and clear the clipboard. Without it, create the secret in the Entra
         admin center, as for the Copilot Studio connector.
      4. With -AssignUser or -AssignGroup, assigns them to the server's enterprise app, which requires assignment.
      5. Prints the Teams developer portal's OAuth client registration fields. That registration has no documented
         API, so you create it by hand. It gives the OAuth client registration ID.
      6. With -OAuthReferenceId and the developer URLs, builds the package with scripts/Build-CoworkPackage.ps1.
         With -Install as well, installs it for you with the Agents Toolkit CLI (atk).

    Requires Azure CLI 2.60+ signed in to the tenant with Application Administrator rights (Cloud Application
    Administrator can't grant admin consent). -WhatIf shows the Entra changes without making them.

.EXAMPLE
    ./Complete-CoworkSetup.ps1 -ResourceGroup rg-taasmcp-test -AssignGroup 'Help desk agents' -CreateSecret
    Sets up the client, assigns the group, copies a new secret to the clipboard, and prints the developer portal values.

.EXAMPLE
    ./Complete-CoworkSetup.ps1 -ResourceGroup rg-taasmcp-test -OAuthReferenceId <OAuth client registration ID> `
        -DeveloperName 'Contoso' -WebsiteUrl https://contoso.com -PrivacyUrl https://contoso.com/privacy `
        -TermsUrl https://contoso.com/terms -Install
    After the developer portal step: builds the package and installs it for you.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $McpServerUrl,
    [string] $ResourceGroup,
    [string] $ContainerAppName,
    [string] $ServerAppName = 'taas-mcp-server',
    [string] $ClientAppName = 'taas-mcp-cowork-client',
    [string] $ScopeName = 'access_as_user',
    [string[]] $AssignUser = @(),
    [string[]] $AssignGroup = @(),
    [switch] $CreateSecret,
    [ValidateRange(1, 24)] [int] $SecretMonths = 6,
    [string] $OAuthReferenceId,
    [string] $DeveloperName,
    [string] $WebsiteUrl,
    [string] $PrivacyUrl,
    [string] $TermsUrl,
    [switch] $Install,
    [switch] $OpenPortal
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$GraphAppId = '00000003-0000-0000-c000-000000000000'   # Microsoft Graph
$RedirectUri = 'https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect'
$SecretName = 'teams-developer-portal'

function Invoke-Az {
    param([string[]] $Arguments)
    $out = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed: $out" }
    return $out
}

function Invoke-GraphPatch([string] $Uri, $Body) {
    $tmp = New-TemporaryFile
    try {
        ($Body | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8
        Invoke-Az @('rest', '--method', 'PATCH', '--uri', $Uri, '--headers', 'Content-Type=application/json', '--body', "@$tmp") | Out-Null
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

function Invoke-GraphPost([string] $Uri, $Body) {
    $tmp = New-TemporaryFile
    try {
        ($Body | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8
        Invoke-Az @('rest', '--method', 'POST', '--uri', $Uri, '--headers', 'Content-Type=application/json', '--body', "@$tmp") | Out-Null
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

function Get-App([string] $DisplayName) {
    Invoke-Az @('ad', 'app', 'list', '--display-name', $DisplayName, '--query', "[?displayName=='$DisplayName'] | [0]", '-o', 'json') | ConvertFrom-Json
}

function Get-ServicePrincipal([string] $AppId) {
    Invoke-Az @('ad', 'sp', 'list', '--filter', "appId eq '$AppId'", '--query', '[0]', '-o', 'json') | ConvertFrom-Json
}

# ------------------------------------------------------------------------------------------------------------------
# 1. The MCP endpoint
# ------------------------------------------------------------------------------------------------------------------
$tenantId = Invoke-Az @('account', 'show', '--query', 'tenantId', '-o', 'tsv')
Write-Host "Tenant $tenantId"

if (-not $McpServerUrl -and $ResourceGroup) {
    $apps = @(Invoke-Az @('containerapp', 'list', '-g', $ResourceGroup, '--query', '[].{name:name, fqdn:properties.configuration.ingress.fqdn}', '-o', 'json') | ConvertFrom-Json)
    if ($ContainerAppName) { $apps = @($apps | Where-Object name -eq $ContainerAppName) }
    if ($apps.Count -ne 1 -or -not $apps[0].fqdn) {
        throw "Expected one container app with ingress in '$ResourceGroup'$(if ($ContainerAppName) { " named '$ContainerAppName'" }), found $($apps.Count). Pass -ContainerAppName or -McpServerUrl."
    }
    $McpServerUrl = "https://$($apps[0].fqdn)/mcp"
}
if ($McpServerUrl) {
    Write-Host "MCP endpoint $McpServerUrl"
    try {
        $probe = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $McpServerUrl -ContentType 'application/json' -Body '{}' -TimeoutSec 60
        Write-Warning "The endpoint answered $($probe.StatusCode) without a token; expected 401. Check it's the Entra-protected deployment."
    }
    catch {
        $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 401) { Write-Host '    requires sign-in (401), as it should' }
        else { Write-Warning "The endpoint didn't answer as expected ($(if ($status) { $status } else { $_.Exception.Message })). It may still be starting from zero; run this again shortly." }
    }
}
else {
    Write-Warning 'No endpoint yet: pass -McpServerUrl or -ResourceGroup. The Entra steps continue; the portal values and build need it.'
}

# ------------------------------------------------------------------------------------------------------------------
# 2. The OAuth client app
# ------------------------------------------------------------------------------------------------------------------
$server = Get-App $ServerAppName
if (-not $server) { throw "No app registration named '$ServerAppName'. Run New-EntraAppRegistrations.ps1 first (docs/setup-entra.md)." }
$serverScope = @($server.api.oauth2PermissionScopes | Where-Object { $_.value -eq $ScopeName -and $_.isEnabled })[0]
if (-not $serverScope) { throw "'$ServerAppName' has no enabled '$ScopeName' scope. Run New-EntraAppRegistrations.ps1 again." }
$offlineAccessId = Invoke-Az @('ad', 'sp', 'show', '--id', $GraphAppId, '--query', "oauth2PermissionScopes[?value=='offline_access'].id | [0]", '-o', 'tsv')

$client = Get-App $ClientAppName
if (-not $client -and $PSCmdlet.ShouldProcess($ClientAppName, 'Create app registration')) {
    $created = Invoke-Az @('ad', 'app', 'create', '--display-name', $ClientAppName, '--sign-in-audience', 'AzureADMyOrg', '-o', 'json') | ConvertFrom-Json
    Write-Host "Created app '$ClientAppName' ($($created.appId))."
    $client = Invoke-Az @('ad', 'app', 'show', '--id', $created.appId, '-o', 'json') | ConvertFrom-Json
}
elseif ($client) { Write-Host "Found app '$ClientAppName' ($($client.appId))." }

if ($client) {
    # Keeps any redirect URIs already there, and any other permissions, replacing only the two resources set here.
    $redirects = @(@($client.web.redirectUris) + $RedirectUri | Where-Object { $_ } | Select-Object -Unique)
    $access = @($client.requiredResourceAccess | Where-Object { $_.resourceAppId -notin $server.appId, $GraphAppId })
    $access += @{ resourceAppId = $server.appId; resourceAccess = @(@{ id = $serverScope.id; type = 'Scope' }) }
    $access += @{ resourceAppId = $GraphAppId; resourceAccess = @(@{ id = $offlineAccessId; type = 'Scope' }) }
    if ($PSCmdlet.ShouldProcess($ClientAppName, "Set redirect URI and permissions ($ScopeName, offline_access)")) {
        Invoke-GraphPatch "https://graph.microsoft.com/v1.0/applications/$($client.id)" @{
            web                    = @{ redirectUris = $redirects }
            requiredResourceAccess = $access
        }
        Write-Host "    redirect URI and permissions set"
    }
    if (-not (Get-ServicePrincipal $client.appId) -and $PSCmdlet.ShouldProcess($ClientAppName, 'Create service principal')) {
        Invoke-Az @('ad', 'sp', 'create', '--id', $client.appId) | Out-Null
    }
    if ($PSCmdlet.ShouldProcess($ClientAppName, 'Grant admin consent')) {
        # Consent isn't checked when the portal registration is created, so without it every sign-in fails later.
        try { Invoke-Az @('ad', 'app', 'permission', 'admin-consent', '--id', $client.appId) | Out-Null; Write-Host '    admin consent granted' }
        catch { Write-Warning "Admin consent failed ($_). Grant it in Entra admin center > App registrations > $ClientAppName > API permissions." }
    }
}

# ------------------------------------------------------------------------------------------------------------------
# 3. The client secret (optional): clipboard only
# ------------------------------------------------------------------------------------------------------------------
$secrets = if ($client) { @(Invoke-Az @('ad', 'app', 'credential', 'list', '--id', $client.appId, '-o', 'json') | ConvertFrom-Json) } else { @() }
foreach ($s in $secrets | Where-Object { $_.endDateTime }) {
    $days = ([datetime] $s.endDateTime - (Get-Date)).Days
    if ($days -lt 30) { Write-Warning "Secret '$($s.displayName)' on $ClientAppName expires in $days days ($($s.endDateTime))." }
}
if ($CreateSecret -and $client) {
    if (-not (Get-Command Set-Clipboard -ErrorAction SilentlyContinue)) {
        throw 'No clipboard here (Set-Clipboard), and the secret is never printed. Create it in the Entra admin center instead.'
    }
    if ($PSCmdlet.ShouldProcess($ClientAppName, "Add a client secret valid for $SecretMonths months")) {
        $end = (Get-Date).AddMonths($SecretMonths).ToString('yyyy-MM-dd')
        # Its own call, not Invoke-Az: warnings on stderr mustn't mix into the value, and a failure's text isn't shown
        # in case it carries the secret.
        $secret = & az ad app credential reset --id $client.appId --append --display-name $SecretName --end-date $end --query password -o tsv 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $secret) { throw 'Creating the client secret failed. Create it in the Entra admin center instead.' }
        Set-Clipboard -Value $secret
        $secret = $null
        Write-Host "    client secret '$SecretName' created (expires $end) and copied to the clipboard. Paste it into the developer portal's Client secret field, then clear the clipboard."
    }
}
elseif ($client -and -not @($secrets | Where-Object { $_.endDateTime -and [datetime] $_.endDateTime -gt (Get-Date) })) {
    Write-Host "    no current client secret: run again with -CreateSecret, or create one in Entra admin center > App registrations > $ClientAppName > Certificates & secrets"
}

# ------------------------------------------------------------------------------------------------------------------
# 4. Who may use the server
# ------------------------------------------------------------------------------------------------------------------
if ($AssignUser.Count -or $AssignGroup.Count) {
    $serverSp = Get-ServicePrincipal $server.appId
    if (-not $serverSp) { throw "'$ServerAppName' has no enterprise app (service principal). Run New-EntraAppRegistrations.ps1 again." }
    $assigned = @(Invoke-Az @('rest', '--method', 'GET', '--uri', "https://graph.microsoft.com/v1.0/servicePrincipals/$($serverSp.id)/appRoleAssignedTo?`$select=principalId&`$top=999", '--query', 'value[].principalId', '-o', 'json') | ConvertFrom-Json)
    $principals = @()
    foreach ($u in $AssignUser) { $principals += [pscustomobject]@{ Name = $u; Id = (Invoke-Az @('ad', 'user', 'show', '--id', $u, '--query', 'id', '-o', 'tsv')) } }
    foreach ($g in $AssignGroup) { $principals += [pscustomobject]@{ Name = $g; Id = (Invoke-Az @('ad', 'group', 'show', '--group', $g, '--query', 'id', '-o', 'tsv')) } }
    foreach ($p in $principals) {
        if ($p.Id -in $assigned) { Write-Host "    $($p.Name) is already assigned to $ServerAppName"; continue }
        if ($PSCmdlet.ShouldProcess($p.Name, "Assign to $ServerAppName")) {
            # The default access role: the server's own app role is for applications, not people.
            Invoke-GraphPost "https://graph.microsoft.com/v1.0/servicePrincipals/$($serverSp.id)/appRoleAssignedTo" @{
                principalId = $p.Id; resourceId = $serverSp.id; appRoleId = '00000000-0000-0000-0000-000000000000'
            }
            Write-Host "    assigned $($p.Name) to $ServerAppName"
        }
    }
}

# ------------------------------------------------------------------------------------------------------------------
# 5. The Teams developer portal registration (by hand)
# ------------------------------------------------------------------------------------------------------------------
$portal = 'https://dev.teams.microsoft.com/tools/oauth-configuration'
if (-not $OAuthReferenceId) {
    $login = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0"
    Write-Host ''
    Write-Host '========== Teams developer portal > Tools > OAuth client registration =========='
    Write-Host "Portal                 : $portal"
    Write-Host "Registration name      : TeamsWork Ticketing (Cowork)"
    Write-Host "Base URL               : $(if ($McpServerUrl) { $McpServerUrl } else { '<the MCP endpoint>' })"
    Write-Host 'Restrict usage by org  : My organization only'
    Write-Host 'Restrict usage by app  : Any Teams app   (one app ID makes every tool call return 404)'
    Write-Host "Client ID              : $(if ($client) { $client.appId } else { "<the client ID of $ClientAppName>" })"
    Write-Host "Client secret          : $(if ($CreateSecret) { 'on your clipboard' } else { "a secret from $ClientAppName (never stored here)" })"
    Write-Host "Authorization endpoint : $login/authorize"
    Write-Host "Token endpoint         : $login/token"
    Write-Host "Refresh endpoint       : $login/token"
    Write-Host "Scope                  : api://$($server.appId)/$ScopeName offline_access"
    Write-Host 'PKCE                   : On'
    Write-Host ''
    Write-Host 'Saving it shows an OAuth client registration ID. Run this again with -OAuthReferenceId <that ID> and the'
    Write-Host 'developer URLs to build the package.'
    if ($OpenPortal) { Start-Process $portal }
    return
}

# ------------------------------------------------------------------------------------------------------------------
# 6. Build, and install
# ------------------------------------------------------------------------------------------------------------------
if (-not $McpServerUrl) { throw 'The build needs the endpoint: pass -McpServerUrl or -ResourceGroup.' }
$missing = @('DeveloperName', 'WebsiteUrl', 'PrivacyUrl', 'TermsUrl' | Where-Object { -not (Get-Variable $_ -ValueOnly) })
if ($missing) { throw "The package needs -$($missing -join ', -') (the organization publishing it)." }

$build = Join-Path $repoRoot 'scripts/Build-CoworkPackage.ps1'
& $build -McpServerUrl $McpServerUrl -OAuthReferenceId $OAuthReferenceId -DeveloperName $DeveloperName `
    -WebsiteUrl $WebsiteUrl -PrivacyUrl $PrivacyUrl -TermsUrl $TermsUrl
$zip = Join-Path $repoRoot 'artifacts/cowork/teamswork-ticketing-cowork.zip'

$atk = if (Get-Command atk -ErrorAction SilentlyContinue) { @('atk') } else { @('npx', '-y', '@microsoft/m365agentstoolkit-cli') }
& $atk[0] @($atk | Select-Object -Skip 1) validate --package-file $zip --interactive false
if ($LASTEXITCODE -ne 0) { throw 'Agents Toolkit validation failed; see above.' }

if ($Install) {
    if ($PSCmdlet.ShouldProcess($zip, 'Install for you (atk install --scope Personal)')) {
        & $atk[0] @($atk | Select-Object -Skip 1) install --file-path $zip --scope Personal --interactive false
        if ($LASTEXITCODE -ne 0) { throw "atk install failed. If you aren't signed in, run: $($atk -join ' ') auth login" }
        Write-Host 'Installed. Keep the TitleId and AppId above for updating or removing it. Then open Cowork > Sources & Skills > Plugins.'
    }
}
else {
    Write-Host "Built and validated $zip. Run again with -Install to install it for you, or upload it in the Microsoft 365 admin center (docs/cowork.md)."
}
