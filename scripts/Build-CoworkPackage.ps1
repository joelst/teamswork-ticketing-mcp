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

.EXAMPLE
    ./scripts/Build-CoworkPackage.ps1 -McpServerUrl https://taasmcp-test-app.example.azurecontainerapps.io/mcp `
        -OAuthReferenceId <auth config ID> -DeveloperName 'Contoso' -WebsiteUrl https://contoso.com `
        -PrivacyUrl https://contoso.com/privacy -TermsUrl https://contoso.com/terms

.EXAMPLE
    ./scripts/Build-CoworkPackage.ps1 -CheckOnly
    Checks the skills and the manifest template without building a package.
#>
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $McpServerUrl,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $OAuthReferenceId,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $DeveloperName,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $WebsiteUrl,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $PrivacyUrl,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [string] $TermsUrl,
    [Parameter(ParameterSetName = 'Build')] [string] $OutputPath,
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
    if (-not ($manifest.id -as [guid])) { $problems.Add('manifest id must be a GUID') }
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
    $text = [regex]::Replace($Template, '\{\{([A-Z_]+)\}\}', {
            param($match)
            $key = $match.Groups[1].Value
            if (-not $Values.ContainsKey($key)) { throw "manifest.json has an unknown placeholder {{$key}}" }
            # The value lands inside a JSON string, so it's escaped as one (ConvertTo-Json adds the quotes).
            (ConvertTo-Json ([string] $Values[$key]) -Compress).Trim('"')
        })
    return $text
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

Assert-HttpsUrl 'McpServerUrl' $McpServerUrl
Assert-HttpsUrl 'WebsiteUrl' $WebsiteUrl
Assert-HttpsUrl 'PrivacyUrl' $PrivacyUrl
Assert-HttpsUrl 'TermsUrl' $TermsUrl
if ($OAuthReferenceId -notmatch '^\S+$') { throw '-OAuthReferenceId must be the auth config ID from the Teams developer portal.' }

$manifestText = Expand-ManifestTemplate (Get-Content -Raw -Encoding UTF8 (Join-Path $pluginRoot 'cowork/manifest.json')) @{
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

Write-Host "Built $zipPath"
Write-Host 'Install it for yourself with: atk install --file-path <that path> --scope Personal (see docs/cowork.md).'
