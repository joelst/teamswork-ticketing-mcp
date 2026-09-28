# Tests install.ps1's secrets helpers without a terminal: the interactive Set-Secrets can't run in CI, so its checks
# live in functions that can. Loads them from the script itself, stubs the network and the Azure CLI, and exits 1 on
# any failure. Runs under Windows PowerShell 5.1 and PowerShell 7 (see .github/workflows/ci.yml).
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\install.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $script), [ref]$null, [ref]$null)
foreach ($name in 'Get-First', 'Get-Setting', 'Test-NestedSecrets', 'Find-Person') {
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

# ---- Get-Setting: as the server's .NET configuration reads it --------------------------------------------------------
# Environment variable names are case-sensitive on Linux and macOS, so each spelling is its own variable there.
$file = [ordered]@{ 'ticketing:baseurl' = 'https://from-file.example/v1'; 'Ticketing:Region' = 'AUS' }
# Deleted with a real null: PowerShell turns $null into "" for a string parameter, which .NET Core keeps as an empty
# variable rather than deleting it.
function Clear-TicketingVariables { foreach ($n in 'Ticketing:BaseUrl', 'Ticketing__BaseUrl', 'TICKETING__BASEURL', 'ticketing__baseurl', 'Ticketing__Region') { [Environment]::SetEnvironmentVariable($n, [NullString]::Value) } }
Clear-TicketingVariables
Check 'the secrets file is read, whatever the case of its keys' ((Get-Setting 'Ticketing:BaseUrl' $file) -eq 'https://from-file.example/v1')
Check 'a value about to be written wins over the file' ((Get-Setting 'Ticketing:Region' $file 'EU') -eq 'EU')
foreach ($spelling in 'Ticketing:BaseUrl', 'Ticketing__BaseUrl', 'TICKETING__BASEURL', 'ticketing__baseurl') {
    Clear-TicketingVariables
    [Environment]::SetEnvironmentVariable($spelling, 'https://from-env.example/v1')
    Check "an environment variable spelled $spelling wins over the file" ((Get-Setting 'Ticketing:BaseUrl' $file) -eq 'https://from-env.example/v1')
}
Clear-TicketingVariables
[Environment]::SetEnvironmentVariable('Ticketing__Region', 'US')
Check 'a region in the environment wins over -Region and the file, as at runtime' ((Get-Setting 'Ticketing:Region' $file 'EU') -eq 'US')
Clear-TicketingVariables
Check 'nothing set anywhere is nothing' ($null -eq (Get-Setting 'Ticketing:BaseUrl' ([ordered]@{})))
# An empty variable can exist (on Windows too, under .NET Core; Windows PowerShell 5.1 deletes one set to nothing).
# .NET configuration keeps it, and it overrides the file and -Region: the server then uses its default region, US, so
# the lookup must see it as set and empty.
[Environment]::SetEnvironmentVariable('Ticketing__Region', '')
if ([Environment]::GetEnvironmentVariables().Contains('Ticketing__Region')) {
    Check 'an empty region variable wins over -Region and the file' ((Get-Setting 'Ticketing:Region' $file 'EU') -eq '')
}
else {
    Write-Host 'skip an empty variable (this PowerShell deletes a variable set to nothing)'
}
Clear-TicketingVariables
Clear-TicketingVariables

# ---- Find-Person -----------------------------------------------------------------------------------------------------
# Stubs, found before the real commands: the network, and no Azure CLI.
$script:requests = @()
$script:answer = $null
function Invoke-RestMethod { param([string] $Uri, [switch] $UseBasicParsing, [int] $TimeoutSec) $script:requests += $Uri; if ($script:answer -is [scriptblock]) { & $script:answer } else { $script:answer } }
function Get-Command { $null }

$script:answer = ('{"item":{"assignees":{"peoples":[' +
    '{"id":"1111aaaa-1111-1111-1111-111111111111","name":"Pat Lee","email":"Pat.Lee@contoso.com"},' +
    '{"id":"22222222-2222-2222-2222-222222222222","name":"Sam Roe","email":"sam@contoso.com"},' +
    '{"id":"33333333-3333-3333-3333-333333333333","name":"Sam Roe (2)","email":"SAM@contoso.com"},' +
    '{"id":"1111AAAA-1111-1111-1111-111111111111","name":"Pat Lee","email":"pat.lee@contoso.com"}]}}}') | ConvertFrom-Json
# Pat is listed twice, the second time with the ID in upper case (install.sh counts the same way): one person, so still one match.

$pat = Find-Person 'pat.lee@contoso.com' 'k' 'EU' ''
Check 'an email is matched in the assignee list, case aside' ($pat.Id -eq '1111aaaa-1111-1111-1111-111111111111' -and $pat.Name -eq 'Pat Lee')
Check 'the regional endpoint is asked' ($script:requests[-1] -like 'https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=k&timezone=0')
Check 'no region means US' ((Find-Person 'pat.lee@contoso.com' 'k' '' '') -and $script:requests[-1] -like 'https://teamswork.azure-api.net/ticketing/v1/*')
# The server trims the region and treats a blank one as unset (US).
Check 'a blank region means US, as for the server' ((Find-Person 'pat.lee@contoso.com' 'k' '   ' '') -and $script:requests[-1] -like 'https://teamswork.azure-api.net/ticketing/v1/*')
Check 'a region with spaces around it is that region' ((Find-Person 'pat.lee@contoso.com' 'k' ' eu ' '') -and $script:requests[-1] -like 'https://ticketing-apim-eu.azure-api.net/ticketing/v1/*')
$full = $script:answer
foreach ($shape in '{"item":{"assignees":null}}', '{"item":{"assignees":{"peoples":null}}}', '{"item":{}}') {
    $script:answer = $shape | ConvertFrom-Json
    Check "no match with $shape" ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' 'US' ''))
}
$script:answer = $full
Check 'two people with one email is no match' ($null -eq (Find-Person 'sam@contoso.com' 'k' 'US' ''))
Check 'nobody with the email is no match' ($null -eq (Find-Person 'nobody@contoso.com' 'k' 'US' ''))

$script:requests = @()
Check 'a custom base URL gets no request, so the key stays put' ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' 'US' 'https://elsewhere.example/v1') -and $script:requests.Count -eq 0)
Check 'an unknown region gets no request' ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' 'XX' '') -and $script:requests.Count -eq 0)
Check 'no key gets no request' ($null -eq (Find-Person 'pat.lee@contoso.com' '' 'US' '') -and $script:requests.Count -eq 0)

$script:answer = { throw 'Failed: https://teamswork.azure-api.net/ticketing/v1/instance?key=super-secret-key&timezone=0' }
$output = Find-Person 'pat.lee@contoso.com' 'super-secret-key' 'US' '' 6>&1 | Out-String
Check 'a failed request is no match' ($output -notmatch '1111aaaa')
Check "a failed request doesn't print the key or the error" ($output -notmatch 'super-secret-key' -and $output -match "couldn't read the help desk's assignee list")

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
