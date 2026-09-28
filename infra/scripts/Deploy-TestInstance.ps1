<#
.SYNOPSIS
    Deploys a test instance of the MCP server to Azure with your own Azure CLI sign-in, without the Azure DevOps
    pipeline: core infrastructure, the Ticketing API key, the container image, the app, and a smoke test.

.DESCRIPTION
    Runs the same steps as the pipeline's Core, Image and Deploy stages (pipelines/azure-pipelines.yml), in its own
    resource group with environmentName 'test' by default, so no names collide with production.

      1. Registers the resource providers the templates use (a no-op when they already are).
      2. Deploys infra/core.bicep: registry, Key Vault, managed identity, logs, Container Apps environment.
      3. Gives you Key Vault Secrets Officer on the vault and AcrPush on the registry (Owner doesn't cover either).
      4. Stores the Ticketing API key with Set-TicketingApiKey.ps1, which asks for it with hidden input, when the vault
         doesn't have it yet (or with -SetApiKey). The app reads it at start-up, so this comes before step 6.
      5. Builds the container image with the .NET SDK (no Docker needed) and pushes it to the registry.
      6. Deploys infra/app.bicep with that image and the server's Entra app registration.
      7. Checks /healthz answers 200 and /mcp answers 401 without a token, and prints the MCP endpoint.

    Run it again to deploy a new image: steps that are already done are skipped or repeated harmlessly.

    Needs Owner (or Contributor plus User Access Administrator) on the resource group or subscription, the .NET 10
    SDK, and Azure CLI 2.60+. The server's app registration comes from New-EntraAppRegistrations.ps1.

.EXAMPLE
    ./Deploy-TestInstance.ps1 -Location <region>
    Deploys to rg-taasmcp-test, finding the taas-mcp-server app registration by name.

.EXAMPLE
    ./Deploy-TestInstance.ps1 -Location <region> -TicketingRegion EU
    For a help desk on the vendor's EU endpoint.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Location,
    [string] $ResourceGroup = 'rg-taasmcp-test',
    [ValidatePattern('^[a-z0-9]{3,12}$')] [string] $NamePrefix = 'taasmcp',
    [ValidatePattern('^[a-z0-9]{2,8}$')] [string] $EnvironmentName = 'test',
    [string] $ServerAppName = 'taas-mcp-server',
    [string] $EntraClientId,
    [ValidateSet('US', 'EU', 'AUS')] [string] $TicketingRegion = 'US',
    [string] $DefaultTimeZoneId = 'America/Chicago',
    [string] $ServiceAccountId = '',
    [string] $ServiceAccountName = '',
    [string] $ServiceAccountEmail = '',
    [string] $ExternalEmailDomains = '',
    [switch] $SetApiKey,
    [switch] $SkipImage
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ticketingBaseUrls = @{
    US  = 'https://teamswork.azure-api.net/ticketing/v1'
    EU  = 'https://ticketing-apim-eu.azure-api.net/ticketing/v1'
    AUS = 'https://ticketing-apim-aus.azure-api.net/ticketing/v1'
}
$imageRepository = 'teamswork-taas-mcp'

# Runs az and returns its standard output. Its standard error (warnings such as a Bicep upgrade notice) is kept apart,
# so JSON output parses, and is shown only when the command fails.
function Invoke-Az {
    param([string[]] $Arguments)
    $stderr = [System.Collections.Generic.List[string]]::new()
    $out = & az @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $stderr.Add($_.ToString()) } else { $_ }
    }
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments[0..([Math]::Min(3, $Arguments.Count - 1))] -join ' ') ... failed: $($stderr -join ' ')" }
    return $out
}

function Step([string] $Text) { Write-Host ''; Write-Host "==> $Text" }

# Gives $Assignee the role at $Scope unless it already has it.
function Grant-Role([string] $Role, [string] $Assignee, [string] $Scope) {
    $existing = Invoke-Az @('role', 'assignment', 'list', '--assignee', $Assignee, '--role', $Role, '--scope', $Scope, '--query', '[0].id', '-o', 'tsv')
    if ($existing) { Write-Host "    you already have $Role"; return $false }
    if ($PSCmdlet.ShouldProcess($Scope, "Assign $Role to you")) {
        Invoke-Az @('role', 'assignment', 'create', '--assignee-object-id', $Assignee, '--assignee-principal-type', 'User', '--role', $Role, '--scope', $Scope, '-o', 'none') | Out-Null
        Write-Host "    assigned $Role (it can take a minute or two to apply)"
        return $true
    }
    return $false
}

# ------------------------------------------------------------------------------------------------------------------
Step 'Sign-in and inputs'
$account = Invoke-Az @('account', 'show', '-o', 'json') | ConvertFrom-Json
$tenantId = $account.tenantId
$me = Invoke-Az @('ad', 'signed-in-user', 'show', '--query', 'id', '-o', 'tsv')
Write-Host "    subscription $($account.name), tenant $tenantId"
if (-not $EntraClientId) {
    $EntraClientId = Invoke-Az @('ad', 'app', 'list', '--display-name', $ServerAppName, '--query', "[?displayName=='$ServerAppName'] | [0].appId", '-o', 'tsv')
    if (-not $EntraClientId) { throw "No app registration named '$ServerAppName'. Run New-EntraAppRegistrations.ps1 first, or pass -EntraClientId." }
}
Write-Host "    server app registration $EntraClientId"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and -not $SkipImage) { throw 'The .NET SDK (dotnet) is needed to build the image.' }

# ------------------------------------------------------------------------------------------------------------------
Step 'Resource providers'
foreach ($provider in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity') {
    $state = Invoke-Az @('provider', 'show', '-n', $provider, '--query', 'registrationState', '-o', 'tsv')
    if ($state -eq 'Registered') { continue }
    if ($PSCmdlet.ShouldProcess($provider, 'Register resource provider')) {
        Write-Host "    registering $provider (a few minutes the first time)"
        Invoke-Az @('provider', 'register', '-n', $provider, '--wait') | Out-Null
    }
}

# ------------------------------------------------------------------------------------------------------------------
Step "Core infrastructure in $ResourceGroup ($Location)"
if (-not $PSCmdlet.ShouldProcess($ResourceGroup, 'Create the resource group and deploy core.bicep and app.bicep')) { return }
Invoke-Az @('group', 'create', '-n', $ResourceGroup, '-l', $Location, '-o', 'none') | Out-Null
$core = Invoke-Az @('deployment', 'group', 'create', '-g', $ResourceGroup, '-n', "core-$(Get-Date -Format yyyyMMddHHmmss)",
    '--template-file', (Join-Path $repoRoot 'infra/core.bicep'),
    '--parameters', "namePrefix=$NamePrefix", "environmentName=$EnvironmentName",
    '--query', 'properties.outputs', '-o', 'json') | ConvertFrom-Json
$vault = $core.keyVaultName.value
$registry = $core.registryName.value
$loginServer = $core.registryLoginServer.value
Write-Host "    vault $vault, registry $loginServer"

# ------------------------------------------------------------------------------------------------------------------
Step 'Your access to the vault and the registry'
$vaultId = Invoke-Az @('keyvault', 'show', '-n', $vault, '--query', 'id', '-o', 'tsv')
$registryId = Invoke-Az @('acr', 'show', '-n', $registry, '--query', 'id', '-o', 'tsv')
# A new vault role is waited for below, by retrying the read.
$null = Grant-Role 'Key Vault Secrets Officer' $me $vaultId
$newRegistryRole = Grant-Role 'AcrPush' $me $registryId

# ------------------------------------------------------------------------------------------------------------------
Step 'Ticketing API key'
# A new role can take a while to apply, so reading the vault is retried for up to three minutes.
$hasKey = $null
for ($i = 0; $i -lt 12; $i++) {
    $names = & az keyvault secret list --vault-name $vault --query '[].name' -o tsv 2>$null
    if ($LASTEXITCODE -eq 0) { $hasKey = @($names) -contains 'Ticketing--ApiKey'; break }
    if ($i -eq 0) { Write-Host '    waiting for the vault role to apply...' }
    Start-Sleep -Seconds 15
}
if ($null -eq $hasKey) { throw "Couldn't read the vault $vault after three minutes. Check you have Key Vault Secrets Officer on it, then run this again." }
if ($hasKey -and -not $SetApiKey) {
    Write-Host '    already stored (run with -SetApiKey to replace it)'
}
else {
    & (Join-Path $PSScriptRoot 'Set-TicketingApiKey.ps1') -KeyVaultName $vault
}

# ------------------------------------------------------------------------------------------------------------------
if ($SkipImage) {
    Step 'Container image (skipped: using the newest one in the registry)'
    $tag = Invoke-Az @('acr', 'repository', 'show-tags', '-n', $registry, '--repository', $imageRepository, '--orderby', 'time_desc', '--top', '1', '-o', 'tsv')
    if (-not $tag) { throw "The registry has no $imageRepository image yet; run without -SkipImage." }
}
else {
    Step 'Container image'
    $tag = "test-$(Get-Date -Format yyyyMMddHHmmss)"
    if ($newRegistryRole) { Write-Host '    waiting a minute for the push role to apply...'; Start-Sleep -Seconds 60 }
    $env:SDK_CONTAINER_REGISTRY_UNAME = '00000000-0000-0000-0000-000000000000'
    try {
        $env:SDK_CONTAINER_REGISTRY_PWORD = Invoke-Az @('acr', 'login', '-n', $registry, '--expose-token', '--query', 'accessToken', '-o', 'tsv')
        dotnet publish (Join-Path $repoRoot 'src/TeamsWork.Ticketing.Mcp/TeamsWork.Ticketing.Mcp.csproj') -c Release --os linux --arch x64 `
            /t:PublishContainer "-p:ContainerRegistry=$loginServer" "-p:ContainerRepository=$imageRepository" "-p:ContainerImageTags=$tag" -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'Building or pushing the image failed; see above. A 401 or 403 from the registry usually means the AcrPush role has not applied yet: run this again in a minute.' }
    }
    finally {
        Remove-Item Env:SDK_CONTAINER_REGISTRY_PWORD -ErrorAction SilentlyContinue
        Remove-Item Env:SDK_CONTAINER_REGISTRY_UNAME -ErrorAction SilentlyContinue
    }
    Write-Host "    pushed $loginServer/${imageRepository}:$tag"
}

# ------------------------------------------------------------------------------------------------------------------
Step 'Container app'
$app = Invoke-Az @('deployment', 'group', 'create', '-g', $ResourceGroup, '-n', "app-$(Get-Date -Format yyyyMMddHHmmss)",
    '--template-file', (Join-Path $repoRoot 'infra/app.bicep'),
    '--parameters',
    "namePrefix=$NamePrefix", "environmentName=$EnvironmentName",
    "containerImage=$loginServer/${imageRepository}:$tag",
    "containerAppsEnvironmentName=$($core.containerAppsEnvironmentName.value)",
    "identityId=$($core.identityId.value)",
    "registryLoginServer=$loginServer",
    "apiKeySecretUri=$($core.apiKeySecretUri.value)",
    "entraTenantId=$tenantId", "entraClientId=$EntraClientId",
    "ticketingBaseUrl=$($ticketingBaseUrls[$TicketingRegion])",
    "defaultTimeZoneId=$DefaultTimeZoneId",
    "serviceAccountId=$ServiceAccountId", "serviceAccountName=$ServiceAccountName", "serviceAccountEmail=$ServiceAccountEmail",
    "externalEmailDomains=$ExternalEmailDomains",
    '--query', 'properties.outputs', '-o', 'json') | ConvertFrom-Json
$base = "https://$($app.containerAppFqdn.value)"

# ------------------------------------------------------------------------------------------------------------------
Step 'Smoke test'
# The app scales to zero, so the first request can take a while.
$healthy = $false
for ($i = 0; $i -lt 30 -and -not $healthy; $i++) {
    try { $healthy = (Invoke-WebRequest -UseBasicParsing "$base/healthz" -TimeoutSec 30).StatusCode -eq 200 } catch { Start-Sleep -Seconds 5 }
}
if ($healthy) { Write-Host '    /healthz answers 200' }
else { Write-Warning "/healthz didn't answer 200 within a few minutes. Check the app's logs: az containerapp logs show -g $ResourceGroup -n $($app.containerAppName.value) --follow" }
try {
    $null = Invoke-WebRequest -UseBasicParsing -Method Post "$base/mcp" -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' -TimeoutSec 30
    Write-Warning '/mcp answered without a token. It should require Entra sign-in; check the Entra settings.'
}
catch {
    $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 0 }
    if ($status -eq 401) { Write-Host '    /mcp requires sign-in (401), as it should' }
    else { Write-Warning "/mcp answered $(if ($status) { $status } else { $_.Exception.Message }), not 401." }
}

Write-Host ''
Write-Host '================ Test instance ================'
Write-Host "Resource group : $ResourceGroup"
Write-Host "Image          : $loginServer/${imageRepository}:$tag"
Write-Host "MCP endpoint   : $($app.mcpEndpoint.value)"
Write-Host ''
Write-Host 'Try it with your own token:'
Write-Host "  `$token = az account get-access-token --resource api://$EntraClientId --query accessToken -o tsv"
Write-Host "  Invoke-RestMethod $($app.mcpEndpoint.value) -Method Post -Headers @{ Authorization = ""Bearer `$token""; Accept = 'application/json, text/event-stream' } -ContentType 'application/json' -Body '{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list"",""params"":{}}'"
Write-Host "Your account must be assigned to $ServerAppName (Complete-CoworkSetup.ps1 -AssignUser does it)."
Write-Host "Next, for Cowork: ./infra/scripts/Complete-CoworkSetup.ps1 -ResourceGroup $ResourceGroup"
Write-Host "To remove it all: az group delete -n $ResourceGroup --yes, then purge the vault: az keyvault purge -n $vault"
