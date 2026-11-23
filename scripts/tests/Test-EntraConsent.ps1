# Tests Grant-DelegatedConsent in infra/scripts/New-EntraAppRegistrations.ps1 and Complete-CoworkSetup.ps1, which write
# a tenant-wide oauth2PermissionGrant through Graph. Each script's own function is loaded from its source and run against
# stubbed az and Graph calls, so nothing reaches a tenant. Exits 1 on any failure. Runs on Windows PowerShell 5.1 and 7.
$ErrorActionPreference = 'Stop'
$scripts = Join-Path $PSScriptRoot '../../infra/scripts'

$failures = 0
function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failures++ }
}

# The function's definition from the script, without running the script.
function Get-FunctionText([string] $Path, [string] $Name) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $null, [ref] $null)
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if (-not $fn) { throw "$Path has no function $Name" }
    return $fn.Extent.Text
}

# The existing grants the stubbed GET returns, as az prints them, and the writes the function makes.
$script:existing = '[]'
$script:recorded = [System.Collections.Generic.List[string]]::new()
function Invoke-Az([string[]] $Arguments) { return $script:existing }
function Invoke-GraphWrite([string] $Method, [string] $Uri, $Body) { $script:recorded.Add("$Method $Uri $($Body.scope)") }
function Invoke-GraphPost([string] $Uri, $Body) { $script:recorded.Add("POST $Uri $($Body.scope)") }
function Invoke-GraphPatch([string] $Uri, $Body) { $script:recorded.Add("PATCH $Uri $($Body.scope)") }

function Grant([string] $existing) {
    $script:existing = $existing
    $script:recorded.Clear()
    Grant-DelegatedConsent -ClientSpId 'client' -ResourceSpId 'server' -Scopes @('access_as_user')
    return @($script:recorded)
}

$two = '[{"id":"g1","resourceId":"other","consentType":"AllPrincipals","scope":"User.Read"},' +
       '{"id":"g2","resourceId":"server","consentType":"AllPrincipals","scope":"offline_access"}]'

foreach ($file in 'New-EntraAppRegistrations.ps1', 'Complete-CoworkSetup.ps1') {
    . ([scriptblock]::Create((Get-FunctionText (Join-Path $scripts $file) 'Grant-DelegatedConsent')))

    $writes = @(Grant '[]')
    Check "${file}: with no grant, one is created" ($writes.Count -eq 1 -and $writes[0] -eq 'POST https://graph.microsoft.com/v1.0/oauth2PermissionGrants access_as_user')

    # Windows PowerShell 5.1 emits a parsed JSON list as one object, which a filter would take whole.
    $writes = @(Grant $two)
    Check "${file}: of several grants, only the server's is updated" ($writes.Count -eq 1 -and $writes[0] -eq 'PATCH https://graph.microsoft.com/v1.0/oauth2PermissionGrants/g2 offline_access access_as_user')

    $writes = @(Grant '[{"id":"g2","resourceId":"server","consentType":"AllPrincipals","scope":"access_as_user"},{"id":"g3","resourceId":"server","consentType":"Principal","scope":""}]')
    Check "${file}: a grant that has the scope is left alone" ($writes.Count -eq 0)
}

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
