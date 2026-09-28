# Tests the Copilot Cowork plugin: that plugin/ passes the checks Microsoft's upload applies, that those checks catch
# the mistakes they're for, and that a built package has the layout and values the upload expects. Loads the
# functions from Build-CoworkPackage.ps1 itself and exits 1 on any failure. Runs on Windows PowerShell 5.1 and 7.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../Build-CoworkPackage.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $script), [ref]$null, [ref]$null)
foreach ($name in 'Read-SkillFrontMatter', 'Test-CoworkPluginSource', 'Expand-ManifestTemplate') {
    $definition = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $definition) { throw "Build-CoworkPackage.ps1 has no function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$failures = 0
function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failures++ }
}

$plugin = (Resolve-Path (Join-Path $PSScriptRoot '../../plugin')).Path
$problems = @(Test-CoworkPluginSource $plugin)
Check "the plugin passes the upload checks$(if ($problems) { ': ' + ($problems -join '; ') })" ($problems.Count -eq 0)

# ---- Broken copies: each check must catch its mistake ------------------------------------------------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) "cowork-test-$([guid]::NewGuid().ToString('N'))"
function New-Copy {
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Copy-Item -Recurse $plugin $tmp
}
function Set-Manifest([scriptblock] $change) {
    $path = Join-Path $tmp 'cowork/manifest.json'
    $m = Get-Content -Raw $path | ConvertFrom-Json
    & $change $m
    $m | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $path
}
function Test-Caught([string] $what, [string] $pattern) {
    $found = @(Test-CoworkPluginSource $tmp) -match $pattern
    Check $what ($found.Count -gt 0)
}
try {
    New-Copy
    $skill = Join-Path $tmp 'skills/ticket-triage/SKILL.md'
    (Get-Content -Raw $skill) -replace '(?m)^name: ticket-triage$', 'name: TicketTriage' | Set-Content -Encoding UTF8 $skill
    Test-Caught 'a name that differs from its folder is caught' 'ASKILL-P006'
    Test-Caught 'a name that is not kebab-case is caught' 'ASKILL-P007'

    New-Copy
    (Get-Content -Raw $skill) -replace '(?ms)^description: \|.*?(?=^license:)', '' | Set-Content -Encoding UTF8 $skill
    Test-Caught 'a missing description is caught' 'ASKILL-P005'

    New-Copy
    (Get-Content -Raw $skill) -replace '^---', '' | Set-Content -Encoding UTF8 $skill
    Test-Caught 'front matter without its opening line is caught' 'ASKILL-P003'

    New-Copy
    Remove-Item $skill
    Test-Caught 'a skill folder without SKILL.md is caught' 'ASKILL-P002'

    New-Copy
    Set-Manifest { param($m) $m.agentSkills += [pscustomobject]@{ folder = './skills/no-such-skill' } }
    Test-Caught 'a skill folder that does not exist is caught' 'ASKILL-P001'

    New-Copy
    Set-Manifest { param($m) $m.agentSkills += $m.agentSkills[0] }
    Test-Caught 'a skill listed twice is caught' 'ASKILL-P008'

    New-Copy
    Set-Manifest { param($m) $m | Add-Member -NotePropertyName packageName -NotePropertyValue 'com.example' }
    Test-Caught 'a root field outside the v1.28 schema is caught' "packageName"

    New-Copy
    Set-Manifest { param($m) $m.agentConnectors[0].toolSource.remoteMcpServer.authorization.referenceId = '' }
    Test-Caught 'an OAuth connector without a referenceId is caught' 'referenceId'

    New-Copy
    Set-Manifest { param($m) $m.version = '0.1.0' }
    Test-Caught 'a version starting with 0 is caught' 'must be like 1.0.0'

    New-Copy
    Set-Manifest { param($m) $m.agentConnectors[0].toolSource.remoteMcpServer.PSObject.Properties.Remove('mcpToolDescription') }
    Test-Caught 'a connector without the tool description the schema requires is caught' 'mcpToolDescription'

    New-Copy
    Remove-Item (Join-Path $tmp 'cowork/tools/teamswork-ticketing-tools.json')
    Test-Caught 'a tool description file missing from cowork/tools is caught' 'tool description'

    New-Copy
    Set-Content -Path (Join-Path $tmp 'skills/ticket-triage/.hidden.md') -Value 'x'
    Test-Caught 'a hidden companion file is caught' 'refuses'

    New-Copy
    Set-Manifest { param($m) $m.agentSkills = @(1..21 | ForEach-Object { [pscustomobject]@{ folder = './skills/ticket-triage' } }) }
    Test-Caught 'more than 20 skills is caught' 'ASKILL-M002'
}
finally { if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp } }

# ---- The tool description matches the server's tools --------------------------------------------------------------
# Cowork reads the tools from tools/list, so a stale file does no harm there, but it should list what the hosted
# endpoint offers: every tool in the source except upload_ticket_files, which is stdio-only. Regenerate it with
# scripts/Update-CoworkToolDescription.ps1.
$sourceTools = @(Get-ChildItem (Join-Path $PSScriptRoot '../../src/TeamsWork.Ticketing.Mcp/Tools') -Filter *.cs |
    Select-String -Pattern '\[McpServerTool\(Name = "([a-z_]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Where-Object { $_ -ne 'upload_ticket_files' } | Sort-Object)
$describedTools = @((Get-Content -Raw -Encoding UTF8 (Join-Path $plugin 'cowork/tools/teamswork-ticketing-tools.json') | ConvertFrom-Json).tools.name | Sort-Object)
Check "the tool description lists the hosted tools ($($sourceTools.Count))" (($sourceTools -join ',') -eq ($describedTools -join ','))

# ---- Expand-ManifestTemplate ---------------------------------------------------------------------------------------
$expanded = Expand-ManifestTemplate '{"a":"{{X}}","b":"{{Y}}"}' @{ X = 'https://example.test/mcp'; Y = 'Quote " and \ back' }
$parsed = $expanded | ConvertFrom-Json
Check 'placeholders are filled in' ($parsed.a -eq 'https://example.test/mcp')
Check 'a value is JSON-escaped, so quotes and backslashes survive' ($parsed.b -eq 'Quote " and \ back')
$threw = $false; try { Expand-ManifestTemplate '{"a":"{{UNKNOWN}}"}' @{} } catch { $threw = $true }
Check 'an unknown placeholder is refused' $threw

# ---- A built package -------------------------------------------------------------------------------------------------
$out = Join-Path ([IO.Path]::GetTempPath()) "cowork-build-$([guid]::NewGuid().ToString('N'))"
try {
    & $script -McpServerUrl 'https://taasmcp-test-app.example.test/mcp' -OAuthReferenceId 'test-auth-config-id' `
        -DeveloperName 'Example Org' -WebsiteUrl 'https://example.test' -PrivacyUrl 'https://example.test/privacy' `
        -TermsUrl 'https://example.test/terms' -OutputPath $out 6>&1 | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $out 'teamswork-ticketing-cowork.zip'))
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        Check 'the package has manifest.json and both icons at its root' ((@('manifest.json', 'color.png', 'outline.png') | Where-Object { $_ -notin $names }).Count -eq 0)
        Check 'each skill is under skills/ with forward slashes' ('skills/ticket-triage/SKILL.md' -in $names -and -not ($names -match '\\'))
        Check 'the tool description the manifest points at is in the package' ('tools/teamswork-ticketing-tools.json' -in $names)
        $reader = [IO.StreamReader]::new(($zip.Entries | Where-Object FullName -eq 'manifest.json').Open())
        try { $manifestText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
    $manifest = $manifestText | ConvertFrom-Json
    Check 'no placeholder is left in the packaged manifest' ($manifestText -notmatch '\{\{')
    Check 'the endpoint and auth config ID are filled in' ($manifest.agentConnectors[0].toolSource.remoteMcpServer.mcpServerUrl -eq 'https://taasmcp-test-app.example.test/mcp' -and
        $manifest.agentConnectors[0].toolSource.remoteMcpServer.authorization.referenceId -eq 'test-auth-config-id')

    $threw = $false
    try { & $script -McpServerUrl 'http://insecure.example.test/mcp' -OAuthReferenceId 'x' -DeveloperName 'x' -WebsiteUrl 'https://x.test' -PrivacyUrl 'https://x.test' -TermsUrl 'https://x.test' -OutputPath $out 6>&1 | Out-Null }
    catch { $threw = $true }
    Check 'a plain http endpoint is refused' $threw
}
finally { if (Test-Path $out) { Remove-Item -Recurse -Force $out } }

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
