<#
.SYNOPSIS
    Builds the Copilot Cowork plugin package (a Microsoft 365 app package .zip) from plugin/.

.DESCRIPTION
    Checks the skills in plugin/skills and the manifest template in plugin/cowork/manifest.json against the rules
    Microsoft's upload validates (see docs/cowork.md), fills in the deployment's values, and writes
    artifacts/cowork/teamswork-ticketing-cowork.zip with manifest.json, the two icons and skills/ at its root.

    The values aren't in the repository: the MCP endpoint comes from the Azure deployment, the OAuth auth config ID
    from the Teams developer portal, and the developer URLs from the organization publishing the plugin. No secret
    goes into the package; the auth config ID only points at the OAuth client stored in Microsoft's token store.

    The app ID is each organization's own too: Microsoft 365 identifies the plugin by it, so two organizations
    building from this repository must not share one. The first build creates a new ID and saves it in
    plugin/cowork/app-id.txt (git-ignored), and later builds reuse it, so an upload updates the plugin rather than
    adding a second one. Pass -AppId to use a specific ID instead, such as the one an existing install was built with.

    To build without repeating the values, keep them in plugin/cowork/cowork.local.json (git-ignored): any value not
    passed is read from it, and -SaveSettings writes the values a build used (the app ID included) into it. -SettingsPath
    points at another file.

.EXAMPLE
    ./scripts/Build-CoworkPackage.ps1 -McpServerUrl https://taasmcp-test-app.example.azurecontainerapps.io/mcp `
        -OAuthReferenceId <auth config ID> -DeveloperName 'Contoso' -WebsiteUrl https://contoso.com `
        -PrivacyUrl https://contoso.com/privacy -TermsUrl https://contoso.com/terms

.EXAMPLE
    ./scripts/Build-CoworkPackage.ps1
    Builds with the values saved in plugin/cowork/cowork.local.json by an earlier build with -SaveSettings.

.EXAMPLE
    ./scripts/Build-CoworkPackage.ps1 -CheckOnly
    Checks the skills and the manifest template without building a package.
#>
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')] [string] $McpServerUrl,
    [Parameter(ParameterSetName = 'Build')] [string] $OAuthReferenceId,
    [Parameter(ParameterSetName = 'Build')] [string] $DeveloperName,
    [Parameter(ParameterSetName = 'Build')] [string] $WebsiteUrl,
    [Parameter(ParameterSetName = 'Build')] [string] $PrivacyUrl,
    [Parameter(ParameterSetName = 'Build')] [string] $TermsUrl,
    [Parameter(ParameterSetName = 'Build')] [string] $AppId,
    [Parameter(ParameterSetName = 'Build')] [string] $OutputPath,
    [Parameter(ParameterSetName = 'Build')] [string] $SettingsPath,
    [Parameter(ParameterSetName = 'Build')] [switch] $SaveSettings,
    [Parameter(Mandatory, ParameterSetName = 'Check')] [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $root 'plugin'

# The name and description from a SKILL.md's YAML front matter, or an error message. Only what the upload checks is
# read: a plain `key: value`, or a `|` or `>` block scalar indented under the key.
function Read-SkillFrontMatter([string] $Text) {
    $lines = $Text -split "`r?`n"
    if ($lines.Count -lt 3 -or $lines[0] -ne '---') { return @{ Error = 'SKILL.md must start with a --- line' } }
    $end = [Array]::IndexOf($lines, '---', 1)
    if ($end -lt 0) { return @{ Error = 'SKILL.md front matter has no closing --- line' } }
    $fields = @{}
    for ($i = 1; $i -lt $end; $i++) {
        if ($lines[$i] -match '^([A-Za-z_][\w-]*):\s*(.*)$') {
            $key = $Matches[1]; $value = $Matches[2].Trim()
            if ($value -in '|', '>', '|-', '>-') {
                $block = @()
                while ($i + 1 -lt $end -and ($lines[$i + 1] -match '^\s+\S' -or $lines[$i + 1] -eq '')) { $i++; $block += $lines[$i].Trim() }
                $value = ($block -join $(if ($value.StartsWith('|')) { "`n" } else { ' ' })).Trim()
            }
            $fields[$key] = $value.Trim('"', "'")
        }
    }
    return $fields
}

# Every problem the upload would reject in the skills and the manifest template, as messages; none means it passes.
# $PluginRoot holds skills/ and cowork/manifest.json.
function Test-CoworkPluginSource([string] $PluginRoot) {
    # The root fields this package uses, all in Microsoft's v1.28 example. The schema sets additionalProperties to
    # false, so a field outside it (packageName, say) fails the upload.
    $allowedRootFields = '$schema', 'manifestVersion', 'version', 'id', 'developer', 'name', 'description', 'icons',
        'accentColor', 'agentSkills', 'agentConnectors'
    $problems = [System.Collections.Generic.List[string]]::new()
    $manifestPath = Join-Path $PluginRoot 'cowork/manifest.json'
    try { $manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json }
    catch { $problems.Add("cowork/manifest.json is not valid JSON: $($_.Exception.Message)"); return $problems }

    foreach ($field in $manifest.PSObject.Properties.Name) {
        if ($field -notin $allowedRootFields) { $problems.Add("manifest field '$field' isn't allowed by the v1.28 schema") }
    }
    if ($manifest.manifestVersion -ne '1.28') { $problems.Add("manifestVersion must be 1.28, not '$($manifest.manifestVersion)'") }
    # Each organization's build gets its own ID (see Resolve-AppId), so the template never carries one.
    if ($manifest.id -cne '{{APP_ID}}') { $problems.Add("manifest id must be the {{APP_ID}} placeholder, not '$($manifest.id)'; the build fills in each organization's own ID") }
    # Agents Toolkit's validation (the upload's rules) refuses a version starting with 0, such as 0.1.0.
    if ([string] $manifest.version -notmatch '^[1-9]\d*(\.\d+){1,2}$') { $problems.Add("manifest version '$($manifest.version)' must be like 1.0.0 and not start with 0") }
    foreach ($icon in $manifest.icons.color, $manifest.icons.outline) {
        if (-not (Test-Path (Join-Path $PluginRoot "cowork/$icon"))) { $problems.Add("icon '$icon' is missing from cowork/") }
    }

    $skills = @($manifest.agentSkills)
    if ($skills.Count -gt 20) { $problems.Add("agentSkills has $($skills.Count) entries; the limit is 20 (ASKILL-M002)") }
    $seen = @{}
    foreach ($entry in $skills) {
        $folder = [string] $entry.folder
        if (-not $folder) { $problems.Add('an agentSkills entry has no folder (ASKILL-M001)'); continue }
        if ($folder.Length -gt 256) { $problems.Add("agentSkills folder '$folder' is over 256 characters (ASKILL-M003)") }
        if ($seen.ContainsKey($folder)) { $problems.Add("agentSkills folder '$folder' is listed twice (ASKILL-P008)") }
        $seen[$folder] = $true
        $relative = $folder -replace '^\./', ''
        $dir = Join-Path $PluginRoot $relative
        $skillFile = Join-Path $dir 'SKILL.md'
        if (-not (Test-Path $dir -PathType Container)) { $problems.Add("skill folder '$folder' doesn't exist (ASKILL-P001)"); continue }
        if (-not (Test-Path $skillFile -PathType Leaf)) { $problems.Add("skill folder '$folder' has no SKILL.md (ASKILL-P002)"); continue }
        $front = Read-SkillFrontMatter (Get-Content -Raw -Encoding UTF8 $skillFile)
        if ($front.Error) { $problems.Add("$folder/SKILL.md: $($front.Error) (ASKILL-P003)"); continue }
        $leaf = Split-Path -Leaf $dir
        if (-not $front.name) { $problems.Add("$folder/SKILL.md has no name (ASKILL-P004)") }
        elseif ($front.name -cne $leaf) { $problems.Add("$folder/SKILL.md name '$($front.name)' doesn't match its folder '$leaf' (ASKILL-P006)") }
        if ($front.name -and ($front.name.Length -gt 64 -or $front.name -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$')) {
            $problems.Add("$folder/SKILL.md name '$($front.name)' isn't kebab-case of 1-64 characters (ASKILL-P007)")
        }
        if (-not $front.description) { $problems.Add("$folder/SKILL.md has no description (ASKILL-P005)") }
        elseif ($front.description.Length -gt 1024) { $problems.Add("$folder/SKILL.md description is over 1024 characters") }

        $companions = @(Get-ChildItem $dir -Recurse -File -Force | Where-Object { $_.FullName -ne (Resolve-Path $skillFile).Path })
        if ($companions.Count -gt 20) { $problems.Add("$folder has $($companions.Count) companion files; the limit is 20") }
        if (($companions | Measure-Object Length -Sum).Sum -gt 10MB) { $problems.Add("$folder companion files total over 10 MB") }
        foreach ($file in $companions) {
            if ($file.Length -gt 5MB) { $problems.Add("$folder/$($file.Name) is over 5 MB") }
            if ($file.Name.StartsWith('.') -or $file.Name -notmatch '^[A-Za-z0-9 ._!-]+$' -or
                $file.BaseName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                $problems.Add("$folder/$($file.Name) has a name the upload refuses")
            }
        }
    }

    $ids = @{}
    foreach ($connector in @($manifest.agentConnectors)) {
        if (-not $connector.id -or -not $connector.displayName) { $problems.Add('a connector needs an id and a displayName') }
        if ($connector.id -and $ids.ContainsKey($connector.id)) { $problems.Add("connector id '$($connector.id)' is used twice") }
        $ids[[string] $connector.id] = $true
        $server = $connector.toolSource.remoteMcpServer
        if (-not $server) { $problems.Add("connector '$($connector.id)' has no toolSource.remoteMcpServer"); continue }
        if (-not $server.mcpServerUrl) { $problems.Add("connector '$($connector.id)' has no mcpServerUrl") }
        # The v1.28 schema requires the tool-description file, though Cowork itself reads the tools from tools/list.
        $toolFile = [string] $server.mcpToolDescription.file
        if (-not $toolFile) { $problems.Add("connector '$($connector.id)' has no mcpToolDescription.file, which the v1.28 schema requires") }
        elseif ($toolFile -notmatch '^(\./)?tools/[^/\\]+\.json$' -or -not (Test-Path (Join-Path $PluginRoot ('cowork/' + ($toolFile -replace '^\./', ''))) -PathType Leaf)) {
            $problems.Add("connector '$($connector.id)' tool description '$toolFile' must be a file under cowork/tools/")
        }
        $auth = $server.authorization
        if ($auth -and $auth.type -ne 'None' -and -not $auth.referenceId) { $problems.Add("connector '$($connector.id)' needs an authorization referenceId") }
        if ($auth -and $auth.type -eq 'None' -and $auth.referenceId) { $problems.Add("connector '$($connector.id)' has a referenceId with type None") }
    }
    return $problems
}

# The manifest template with its {{PLACEHOLDERS}} replaced, JSON-escaped; throws if one is left or unknown.
function Expand-ManifestTemplate([string] $Template, [hashtable] $Values) {
    $unexpanded = [regex]::Replace($Template, '\{\{[^{}]+\}\}', '')
    if ($unexpanded -match '\{\{|\}\}') { throw 'manifest.json contains a malformed placeholder.' }
    $text = [regex]::Replace($Template, '\{\{([^{}]+)\}\}', {
            param($match)
            $key = $match.Groups[1].Value
            if (-not $Values.ContainsKey($key)) { throw "manifest.json has an unknown placeholder {{$key}}" }
            # The value lands inside a JSON string, so it's escaped as one (ConvertTo-Json adds the quotes).
            $serialized = ConvertTo-Json ([string] $Values[$key]) -Compress
            if ($serialized.Length -lt 2 -or $serialized[0] -ne '"' -or $serialized[$serialized.Length - 1] -ne '"') {
                throw "manifest.json placeholder {{$key}} did not serialize as a JSON string."
            }
            $serialized.Substring(1, $serialized.Length - 2)
        })
    return $text
}

# The plugin's app ID: -AppId when given, else the one saved in $IdFile, else a new one saved there for later builds.
# An explicit -AppId isn't saved, so a test or one-off build can't replace the ID an organization's installs use.
function Resolve-AppId([string] $Given, [string] $IdFile) {
    if ($Given) {
        $id = $Given -as [guid]
        if (-not $id -or $id -eq [guid]::Empty) { throw "-AppId must be a GUID, not '$Given'." }
        return $id.ToString()
    }
    if (Test-Path -LiteralPath $IdFile -PathType Leaf) {
        $saved = (Get-Content -Raw -LiteralPath $IdFile).Trim()
        $id = $saved -as [guid]
        if (-not $id -or $id -eq [guid]::Empty) { throw "$IdFile must hold the plugin's app ID (a GUID), not '$saved'." }
        return $id.ToString()
    }
    $id = [guid]::NewGuid().ToString()
    Set-Content -LiteralPath $IdFile -Value $id -Encoding ascii
    Write-Host "Created app ID $id for this plugin and saved it in $IdFile (git-ignored). Keep that file: later builds"
    Write-Host 'reuse it, so an upload updates the plugin instead of adding a second one.'
    return $id
}

# The names a settings file holds, as the build's parameters are named.
function Get-CoworkSettingNames { 'AppId', 'McpServerUrl', 'OAuthReferenceId', 'DeveloperName', 'WebsiteUrl', 'PrivacyUrl', 'TermsUrl' }

# $Given with each value it lacks taken from the settings file at $Path, when there is one.
function Merge-CoworkSettings([hashtable] $Given, [string] $Path) {
    $merged = @{}
    foreach ($name in $Given.Keys) { $merged[$name] = $Given[$name] }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $merged }
    try { $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json }
    catch { throw "$Path isn't valid JSON: $($_.Exception.Message)" }
    foreach ($name in Get-CoworkSettingNames) {
        $value = $saved.$name
        if (-not $merged[$name] -and $value) { $merged[$name] = [string] $value }
    }
    return $merged
}

# Writes the values a build used to the settings file at $Path, for later builds.
function Save-CoworkSettings([hashtable] $Values, [string] $Path) {
    $ordered = [ordered]@{}
    foreach ($name in Get-CoworkSettingNames) { $ordered[$name] = $Values[$name] }
    $json = ConvertTo-Json ([pscustomobject] $ordered)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Assert-HttpsUrl([string] $Name, [string] $Value) {
    $uri = $null
    if (-not ([Uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -and $uri.Scheme -eq 'https')) {
        throw "-$Name must be an absolute https URL, not '$Value'."
    }
}

$problems = @(Test-CoworkPluginSource $pluginRoot)
if ($problems) {
    $problems | ForEach-Object { Write-Host "  $_" }
    throw "The plugin has $($problems.Count) problem(s) the upload would reject."
}
if ($CheckOnly) { Write-Host 'Plugin skills and manifest template pass the upload checks.'; return }

if (-not $SettingsPath) { $SettingsPath = Join-Path $pluginRoot 'cowork/cowork.local.json' }
$given = @{}
foreach ($name in Get-CoworkSettingNames) { $given[$name] = (Get-Variable -Name $name -ValueOnly) }
$values = Merge-CoworkSettings $given $SettingsPath
$missing = @(Get-CoworkSettingNames | Where-Object { $_ -ne 'AppId' -and -not $values[$_] })
if ($missing) {
    throw "Missing $($missing -join ', '). Pass them (-$($missing[0]) ...), or save them once with -SaveSettings so later builds read them from $SettingsPath."
}
$McpServerUrl = $values.McpServerUrl; $OAuthReferenceId = $values.OAuthReferenceId; $DeveloperName = $values.DeveloperName
$WebsiteUrl = $values.WebsiteUrl; $PrivacyUrl = $values.PrivacyUrl; $TermsUrl = $values.TermsUrl

Assert-HttpsUrl 'McpServerUrl' $McpServerUrl
Assert-HttpsUrl 'WebsiteUrl' $WebsiteUrl
Assert-HttpsUrl 'PrivacyUrl' $PrivacyUrl
Assert-HttpsUrl 'TermsUrl' $TermsUrl
if ($OAuthReferenceId -notmatch '^\S+$') { throw '-OAuthReferenceId must be the auth config ID from the Teams developer portal.' }
$resolvedAppId = Resolve-AppId $values.AppId (Join-Path $pluginRoot 'cowork/app-id.txt')

$manifestText = Expand-ManifestTemplate (Get-Content -Raw -Encoding UTF8 (Join-Path $pluginRoot 'cowork/manifest.json')) @{
    APP_ID             = $resolvedAppId
    MCP_SERVER_URL     = $McpServerUrl
    OAUTH_REFERENCE_ID = $OAuthReferenceId
    DEVELOPER_NAME     = $DeveloperName
    WEBSITE_URL        = $WebsiteUrl
    PRIVACY_URL        = $PrivacyUrl
    TERMS_URL          = $TermsUrl
}
$null = $manifestText | ConvertFrom-Json   # still valid JSON after the values went in

if (-not $OutputPath) { $OutputPath = Join-Path $root 'artifacts/cowork' }
New-Item -ItemType Directory -Force $OutputPath | Out-Null
$zipPath = Join-Path $OutputPath 'teamswork-ticketing-cowork.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath }

# Written entry by entry with forward slashes: Windows PowerShell 5.1's Compress-Archive writes backslashes into entry
# names, which other platforms read as part of the file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $zip.CreateEntry('manifest.json')
    $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($manifestText) } finally { $writer.Dispose() }
    foreach ($icon in 'color.png', 'outline.png') {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $pluginRoot "cowork/$icon"), $icon)
    }
    foreach ($file in Get-ChildItem (Join-Path $pluginRoot 'cowork/tools') -File) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "tools/$($file.Name)")
    }
    $skillsRoot = (Resolve-Path (Join-Path $pluginRoot 'skills')).Path
    foreach ($file in Get-ChildItem $skillsRoot -Recurse -File) {
        $name = 'skills/' + $file.FullName.Substring($skillsRoot.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name)
    }
}
finally { $zip.Dispose() }

if ($SaveSettings) {
    $values.AppId = $resolvedAppId
    Save-CoworkSettings $values $SettingsPath
    Write-Host "Saved these values in $SettingsPath (keep it out of source control; .gitignore lists the default path)."
}

Write-Host "Built $zipPath (app ID $resolvedAppId)"
Write-Host 'Install it for yourself with: atk install --file-path <that path> --scope Personal (see docs/cowork.md).'
