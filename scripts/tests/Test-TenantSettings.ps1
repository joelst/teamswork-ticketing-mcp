# Tests infra/scripts/Read-TenantSettings.ps1, which reads the deployment scripts' values from infra/tenant.local.json,
# and its use in Deploy-TestInstance.ps1 and New-EntraAppRegistrations.ps1. Every case of those scripts stops before
# its first az call. The repository's own infra/tenant.local.json is never read: each case passes a file of its own,
# and the default path is tested on a copy of the reader. Exits 1 on any failure. Runs on Windows PowerShell 5.1 and 7.
$ErrorActionPreference = 'Stop'
$scripts = Join-Path $PSScriptRoot '../../infra/scripts'
$reader = Join-Path $scripts 'Read-TenantSettings.ps1'

$failures = 0
function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failures++ }
}

function Error-Of([scriptblock] $Block) {
    try { & $Block 6>&1 | Out-Null; return '' } catch { return $_.Exception.Message }
}

$out = Join-Path ([IO.Path]::GetTempPath()) ("tenant-settings-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $out | Out-Null
try {
    # A stand-in for a deployment script: the reader takes its section name and parameters from it.
    $target = Join-Path $out 'Deploy-Example.ps1'
    Set-Content -LiteralPath $target -Value @'
[CmdletBinding(SupportsShouldProcess)]
param([string] $Location, [ValidateSet('US', 'EU')] [string] $Region = 'US', [switch] $Flag, [string] $SettingsPath)
'@
    $command = Get-Command $target
    function Write-Settings([string] $Json) {
        $path = Join-Path $out 'settings.local.json'
        [IO.File]::WriteAllText($path, $Json, [Text.UTF8Encoding]::new($false))
        return $path
    }
    function Read([hashtable] $Bound, [string] $Path) { & $reader -Command $command -Bound $Bound -Path $Path 6>$null }

    $path = Write-Settings '{ "Deploy-Example": { "Location": "westus", "Region": "EU", "Flag": true }, "Other-Script": { "Anything": 1 } }'
    $values = Read @{} $path
    Check 'values not passed come from the script''s section' ($values.Location -eq 'westus' -and $values.Region -eq 'EU' -and $values.Flag -eq $true)
    Check 'other scripts'' sections are left alone' ($values.Count -eq 3)

    $values = Read @{ Location = 'eastus'; Flag = $false } $path
    Check 'a value passed wins over the file' (-not $values.ContainsKey('Location') -and -not $values.ContainsKey('Flag') -and $values.Region -eq 'EU')

    $path = Write-Settings '{ "Deploy-Example": { "location": "westus" } }'
    Check 'names match without regard to case' ((Read @{} $path).Location -eq 'westus')

    $path = Write-Settings '{ "Other-Script": { "TenantId": "x" } }'
    Check 'a file without the script''s section gives nothing' ((Read @{} $path).Count -eq 0)

    $path = Write-Settings '{ "Deploy-Example": { "Locaton": "westus", "Location": "westus" } }'
    $message = Error-Of { Read @{} $path }
    Check 'a misspelt name is refused, naming it and what the script takes' ($message -match 'Locaton' -and $message -match 'takes: Location, Region, Flag\.')

    foreach ($name in 'SettingsPath', 'WhatIf', 'Verbose') {
        $path = Write-Settings "{ ""Deploy-Example"": { ""$name"": true } }"
        Check "-$name can't be set from the file" ((Error-Of { Read @{} $path }) -match [regex]::Escape($name))
    }

    $path = Write-Settings '{ "Deploy-Example": { "Location": '
    Check 'a file that isn''t JSON is refused' ((Error-Of { Read @{} $path }) -match 'valid JSON')
    $path = Write-Settings '[ { "Deploy-Example": {} } ]'
    Check 'a file that isn''t an object is refused' ((Error-Of { Read @{} $path }) -match 'must hold a JSON object')
    $path = Write-Settings '{ "Deploy-Example": "westus" }'
    Check 'a section that isn''t an object is refused' ((Error-Of { Read @{} $path }) -match 'must be an object')

    Check 'a -Path that doesn''t exist is refused' ((Error-Of { Read @{} (Join-Path $out 'missing.json') }) -match 'doesn''t exist')
    Push-Location $out
    try {
        Write-Settings '{ "Deploy-Example": { "Location": "relative" } }' | Out-Null
        Check 'a relative -Path resolves from the current PowerShell location' ((Read @{} 'settings.local.json').Location -eq 'relative')
    }
    finally { Pop-Location }

    # The default file, infra/tenant.local.json beside the reader's folder, on a copy so the real one isn't read.
    $copy = Join-Path $out 'infra/scripts'
    New-Item -ItemType Directory -Force $copy | Out-Null
    Copy-Item $reader $copy
    $copied = Join-Path $copy 'Read-TenantSettings.ps1'
    Check 'with no default file there are no values' ((& $copied -Command $command -Bound @{} 6>$null).Count -eq 0)
    [IO.File]::WriteAllText((Join-Path $out 'infra/tenant.local.json'), '{ "Deploy-Example": { "Location": "default" } }')
    Check 'the default file is read when there is one' ((& $copied -Command $command -Bound @{} 6>$null).Location -eq 'default')

    # The scripts themselves: each case stops before its first az call.
    $deploy = Join-Path $scripts 'Deploy-TestInstance.ps1'
    $entra = Join-Path $scripts 'New-EntraAppRegistrations.ps1'

    $path = Write-Settings '{ "Deploy-TestInstance": { "Location": "westus" } }'
    $message = Error-Of { & $deploy -SettingsPath $path }
    Check 'the deploy names a required value neither passed nor in the file' ($message -match 'Missing -ExternalEmailDomains\.' -and $message -notmatch 'Location')

    # The file's domains reach the script's own check, which refuses these before anything is deployed.
    $path = Write-Settings '{ "Deploy-TestInstance": { "Location": "westus", "ExternalEmailDomains": "," } }'
    Check 'the deploy checks domains read from the file' ((Error-Of { & $deploy -SettingsPath $path }) -match 'names no domain')
    $path = Write-Settings '{ "Deploy-TestInstance": { "Location": "westus", "ExternalEmailDomains": "contoso.com" } }'
    Check 'a domain passed wins over the file''s' ((Error-Of { & $deploy -SettingsPath $path -ExternalEmailDomains '@ ;' }) -match 'names no domain')

    foreach ($name in 'SkipImage', 'SetApiKey') {
        $path = Write-Settings "{ ""Deploy-TestInstance"": { ""Location"": ""westus"", ""ExternalEmailDomains"": ""contoso.com"", ""$name"": true } }"
        Check "the deploy refuses -$name from the file, as it is for one run" ((Error-Of { & $deploy -SettingsPath $path }) -match "pass -$name on the command line")
    }

    $path = Write-Settings '{ "Deploy-TestInstance": { "Location": "westus", "ExternalEmailDomains": "contoso.com", "TicketingRegion": "Mars" } }'
    Check 'a value from the file is validated as the parameter would be' ((Error-Of { & $deploy -SettingsPath $path }) -match 'Mars')

    # -WhatIf previews with the file's values: Set-Variable honours -WhatIf, which would skip them.
    $path = Write-Settings '{ "Deploy-TestInstance": { "Location": "westus", "ExternalEmailDomains": "," } }'
    Check 'with -WhatIf the deploy still reads the file' ((Error-Of { & $deploy -SettingsPath $path -WhatIf }) -match 'names no domain')
    # (New-EntraAppRegistrations has no check that fails on a value, and with one that passes it would go on to az.)
    $path = Write-Settings '{ "Complete-CoworkSetup": { "SecretMonths": 99 } }'
    Check 'with -WhatIf the Cowork setup still reads the file' ((Error-Of { & (Join-Path $scripts 'Complete-CoworkSetup.ps1') -SettingsPath $path -WhatIf }) -match '99')

    # The deploy stores the key under the name the app reads, whatever the settings file gives Set-TicketingApiKey.
    $deployText = Get-Content -Raw $deploy
    Check 'the deploy fixes the API key''s secret name' ($deployText -match "Set-TicketingApiKey\.ps1'\)[^\r\n]*-SecretName 'Ticketing--ApiKey'")

    $path = Write-Settings '{ "New-EntraAppRegistrations": { "ServerAppName": "taas-mcp-server" } }'
    Check 'the Entra script names a missing tenant ID' ((Error-Of { & $entra -SettingsPath $path }) -match 'Missing -TenantId')
    $path = Write-Settings '{ "New-EntraAppRegistrations": { "TenantID": "x", "PreAuthorizeCli": true } }'
    Check 'the Entra script refuses a name it takes no parameter for' ((Error-Of { & $entra -SettingsPath $path }) -match 'PreAuthorizeCli')

    $cowork = Join-Path $scripts 'Complete-CoworkSetup.ps1'
    foreach ($name in 'CreateSecret', 'Install', 'OpenPortal') {
        $path = Write-Settings "{ ""Complete-CoworkSetup"": { ""ResourceGroup"": ""rg-test"", ""$name"": true } }"
        Check "the Cowork setup refuses -$name from the file, as it is for one run" ((Error-Of { & $cowork -SettingsPath $path }) -match "pass -$name on the command line")
    }
    $path = Write-Settings '{ "Complete-CoworkSetup": { "SecretMonths": 99 } }'
    Check 'the Cowork setup validates a value from the file' ((Error-Of { & $cowork -SettingsPath $path }) -match '99')

    $apiKey = Join-Path $scripts 'Set-TicketingApiKey.ps1'
    $path = Write-Settings '{ "Set-TicketingApiKey": { "SecretName": "Ticketing--ApiKey" } }'
    Check 'the API key script names a missing vault before asking for the key' ((Error-Of { & $apiKey -SettingsPath $path }) -match 'Missing -KeyVaultName')
    $path = Write-Settings '{ "Set-TicketingApiKey": { "ApiKey": "not here" } }'
    Check 'the API key script takes no key from the file' ((Error-Of { & $apiKey -SettingsPath $path }) -match 'takes no parameter for')
}
finally {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
