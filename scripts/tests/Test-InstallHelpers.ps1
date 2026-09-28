# Tests install.ps1's secrets helpers without a terminal: the interactive Set-Secrets can't run in CI, so its checks
# live in functions that can. Loads them from the script itself, stubs the network and the Azure CLI, and exits 1 on
# any failure. Runs under Windows PowerShell 5.1 and PowerShell 7 (see .github/workflows/ci.yml).
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\install.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $script), [ref]$null, [ref]$null)
foreach ($name in 'Get-First', 'Test-NestedSecrets', 'Find-Person') {
    $definition = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $definition) { throw "install.ps1 has no function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$failures = 0
function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failures++ }
}

# ---- Test-NestedSecrets ----------------------------------------------------------------------------------------------
Check 'flat strings, a number and a boolean are flat' (-not (Test-NestedSecrets ('{"Ticketing:ApiKey":"k","Ticketing:ServiceAccount:Id":"i","Ticketing:MaxScanTickets":500,"Ticketing:Flag":true}' | ConvertFrom-Json)))
Check 'a nested object is found' (Test-NestedSecrets ('{"Ticketing":{"ApiKey":"k"}}' | ConvertFrom-Json))
Check 'an array is found' (Test-NestedSecrets ('{"Ticketing:Domains":["a.com","b.com"]}' | ConvertFrom-Json))
Check 'a one-item array is found' (Test-NestedSecrets ('{"Ticketing:Domains":["a.com"]}' | ConvertFrom-Json))
Check 'an empty file is flat' (-not (Test-NestedSecrets ('{}' | ConvertFrom-Json)))
Check 'nothing is flat' (-not (Test-NestedSecrets $null))

# ---- Find-Person -----------------------------------------------------------------------------------------------------
# Stubs, found before the real commands: the network, and no Azure CLI.
$script:requests = @()
$script:answer = $null
function Invoke-RestMethod { param([string] $Uri, [switch] $UseBasicParsing, [int] $TimeoutSec) $script:requests += $Uri; if ($script:answer -is [scriptblock]) { & $script:answer } else { $script:answer } }
function Get-Command { $null }

$script:answer = ('{"item":{"assignees":{"peoples":[' +
    '{"id":"11111111-1111-1111-1111-111111111111","name":"Pat Lee","email":"Pat.Lee@contoso.com"},' +
    '{"id":"22222222-2222-2222-2222-222222222222","name":"Sam Roe","email":"sam@contoso.com"},' +
    '{"id":"33333333-3333-3333-3333-333333333333","name":"Sam Roe (2)","email":"SAM@contoso.com"}]}}}') | ConvertFrom-Json

$pat = Find-Person 'pat.lee@contoso.com' 'k' 'EU' ''
Check 'an email is matched in the assignee list, case aside' ($pat.Id -eq '11111111-1111-1111-1111-111111111111' -and $pat.Name -eq 'Pat Lee')
Check 'the regional endpoint is asked' ($script:requests[-1] -like 'https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=k&timezone=0')
Check 'no region means US' ((Find-Person 'pat.lee@contoso.com' 'k' '' '') -and $script:requests[-1] -like 'https://teamswork.azure-api.net/ticketing/v1/*')
Check 'two people with one email is no match' ($null -eq (Find-Person 'sam@contoso.com' 'k' 'US' ''))
Check 'nobody with the email is no match' ($null -eq (Find-Person 'nobody@contoso.com' 'k' 'US' ''))

$script:requests = @()
Check 'a custom base URL gets no request, so the key stays put' ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' 'US' 'https://elsewhere.example/v1') -and $script:requests.Count -eq 0)
Check 'an unknown region gets no request' ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' 'XX' '') -and $script:requests.Count -eq 0)
Check 'no key gets no request' ($null -eq (Find-Person 'pat.lee@contoso.com' '' 'US' '') -and $script:requests.Count -eq 0)

$script:answer = { throw 'Failed: https://teamswork.azure-api.net/ticketing/v1/instance?key=super-secret-key&timezone=0' }
$output = Find-Person 'pat.lee@contoso.com' 'super-secret-key' 'US' '' 6>&1 | Out-String
Check 'a failed request is no match' ($output -notmatch '11111111')
Check "a failed request doesn't print the key or the error" ($output -notmatch 'super-secret-key' -and $output -match "couldn't read the help desk's assignee list")

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
