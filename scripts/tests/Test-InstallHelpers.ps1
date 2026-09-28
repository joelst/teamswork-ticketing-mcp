# Tests install.ps1's secrets helpers without a terminal: the interactive Set-Secrets can't run in CI, so its checks
# live in functions that can. Loads them from the script itself, stubs the network and the Azure CLI, and exits 1 on
# any failure. Runs under Windows PowerShell 5.1 and PowerShell 7 (see .github/workflows/ci.yml).
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\install.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $script), [ref]$null, [ref]$null)
foreach ($name in 'Get-First', 'Get-Setting', 'Test-NestedSecrets', 'Resolve-VendorEndpoint', 'Find-Person') {
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

# ---- Resolve-VendorEndpoint: the server's startup rules --------------------------------------------------------------
$us = 'https://teamswork.azure-api.net/ticketing/v1'; $eu = 'https://ticketing-apim-eu.azure-api.net/ticketing/v1'; $aus = 'https://ticketing-apim-aus.azure-api.net/ticketing/v1'
Check 'nothing set is US' ((Resolve-VendorEndpoint '' $null) -eq $us)
Check 'a region picks its endpoint' ((Resolve-VendorEndpoint 'eu' $null) -eq $eu)
Check 'a blank region means US, as for the server' ((Resolve-VendorEndpoint '   ' $null) -eq $us)
Check 'a region with spaces around it is that region' ((Resolve-VendorEndpoint ' eu ' $null) -eq $eu)
Check 'the server trims a region only at the ends, so E U is unknown: no request' ($null -eq (Resolve-VendorEndpoint 'E U' $null))
Check 'an unknown region: no request' ($null -eq (Resolve-VendorEndpoint 'XX' $null))
Check 'the built-in US base URL, set, is a placeholder a region replaces' ((Resolve-VendorEndpoint 'EU' $us) -eq $eu)
Check 'in any case and with a trailing slash' ((Resolve-VendorEndpoint 'AUS' 'HTTPS://teamswork.azure-api.net/ticketing/v1/') -eq $aus)
Check 'the US base URL alone is US' ((Resolve-VendorEndpoint '' "$us/") -eq $us)
Check 'a vendor endpoint set as the base URL is used' ((Resolve-VendorEndpoint '' $eu) -eq $eu)
Check 'and agrees with its own region' ((Resolve-VendorEndpoint ' eu ' $eu) -eq $eu)
Check 'a region and a base URL naming different endpoints stop the server: no request' ($null -eq (Resolve-VendorEndpoint 'EU' $aus))
Check 'a custom base URL: no request' ($null -eq (Resolve-VendorEndpoint '' 'https://elsewhere.example/v1'))
Check 'nor with a region' ($null -eq (Resolve-VendorEndpoint 'EU' 'https://elsewhere.example/v1'))
Check 'an empty base URL: no request' ($null -eq (Resolve-VendorEndpoint '' ''))
# As install.ps1 calls it, with what Get-Setting reads: an unset base URL must reach it as $null, not "".
function Resolve-FromSettings($secrets) { Resolve-VendorEndpoint (Get-Setting 'Ticketing:Region' $secrets) (Get-Setting 'Ticketing:BaseUrl' $secrets) }
Clear-TicketingVariables
Check 'end to end, the built-in URL in the file with a region is the regional endpoint' ((Resolve-FromSettings ([ordered]@{ 'Ticketing:BaseUrl' = $us; 'Ticketing:Region' = 'EU' })) -eq $eu)
Check 'end to end, nothing set is US' ((Resolve-FromSettings ([ordered]@{})) -eq $us)
[Environment]::SetEnvironmentVariable('Ticketing__BaseUrl', '')
if ([Environment]::GetEnvironmentVariables().Contains('Ticketing__BaseUrl')) {
    Check 'end to end, an empty base URL variable: no request' ($null -eq (Resolve-FromSettings ([ordered]@{})))
}
else {
    Write-Host 'skip an empty variable (this PowerShell deletes a variable set to nothing)'
}
Clear-TicketingVariables

# ---- Find-Person -----------------------------------------------------------------------------------------------------
# Stubs, found before the real commands: the network, and no Azure CLI.
$script:requests = @()
$script:answer = $null
function Invoke-RestMethod { param([string] $Uri, [switch] $UseBasicParsing, [int] $TimeoutSec) $script:requests += $Uri; if ($script:answer -is [scriptblock]) { & $script:answer } else { $script:answer } }
function Get-Command { $null }

# Pat is listed twice, the second time with the ID in upper case (install.sh counts the same way): one person, so still
# one match. A list of the same name nested under a custom field, and one outside item, don't count.
$script:answer = ('{"before":{"assignees":{"peoples":[{"id":"88888888-8888-8888-8888-888888888888","name":"Root","email":"root@contoso.com"}]}},' +
    '"item":{"customFieldsLeft":[{"id":"f","extension":{"assignees":{"peoples":[{"id":"99999999-1111-0000-0000-000000000000","name":"Only Nested","email":"nested@contoso.com"}]}}}],' +
    '"assignees":{"peoples":[' +
    '{"id":"1111aaaa-1111-1111-1111-111111111111","name":"Pat Lee","email":"Pat.Lee@contoso.com"},' +
    '{"id":"22222222-2222-2222-2222-222222222222","name":"Sam Roe","email":"sam@contoso.com"},' +
    '{"id":"33333333-3333-3333-3333-333333333333","name":"Sam Roe (2)","email":"SAM@contoso.com"},' +
    '{"id":"1111AAAA-1111-1111-1111-111111111111","name":"Pat Lee","email":"pat.lee@contoso.com"}]}}}') | ConvertFrom-Json

$pat = Find-Person 'pat.lee@contoso.com' 'k' $eu
Check 'an email is matched in the assignee list, case aside' ($pat.Id -eq '1111aaaa-1111-1111-1111-111111111111' -and $pat.Name -eq 'Pat Lee')
Check 'the endpoint given is asked' ($script:requests[-1] -like "$eu/instance?key=k&timezone=0")
Check 'a nested list of the same name is not the assignee list' ($null -eq (Find-Person 'nested@contoso.com' 'k' $us))
Check 'nor is one outside item' ($null -eq (Find-Person 'root@contoso.com' 'k' $us))
$full = $script:answer
foreach ($shape in '{"item":{"assignees":null}}', '{"item":{"assignees":{"peoples":null}}}', '{"item":{}}') {
    $script:answer = $shape | ConvertFrom-Json
    Check "no match with $shape" ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' $us))
}
$script:answer = $full
Check 'two people with one email is no match' ($null -eq (Find-Person 'sam@contoso.com' 'k' $us))
Check 'nobody with the email is no match' ($null -eq (Find-Person 'nobody@contoso.com' 'k' $us))

$script:requests = @()
Check 'no endpoint (custom, conflicting, unknown) gets no request, so the key stays put' ($null -eq (Find-Person 'pat.lee@contoso.com' 'k' $null) -and $script:requests.Count -eq 0)
Check 'no key gets no request' ($null -eq (Find-Person 'pat.lee@contoso.com' '' $us) -and $script:requests.Count -eq 0)

$script:answer = { throw 'Failed: https://teamswork.azure-api.net/ticketing/v1/instance?key=super-secret-key&timezone=0' }
$output = Find-Person 'pat.lee@contoso.com' 'super-secret-key' $us 6>&1 | Out-String
Check 'a failed request is no match' ($output -notmatch '1111aaaa')
Check "a failed request doesn't print the key or the error" ($output -notmatch 'super-secret-key' -and $output -match "couldn't read the help desk's assignee list")
if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
