<#
.SYNOPSIS
    Installs the TeamsWork Ticketing MCP server on Windows and registers it with local MCP clients.

.DESCRIPTION
    Downloads a release from GitHub, checks it against the release's SHA256SUMS.txt, and puts the executable at a
    fixed per-user path (default %LOCALAPPDATA%\Programs\teamswork-ticketing-mcp), so client configurations keep
    working across upgrades. Run it again to upgrade.

    The API key and the account ticket changes are recorded under go in the .NET user-secrets file the server reads
    (%APPDATA%\Microsoft\UserSecrets\teamswork-taas-mcp\secrets.json). They are never written to a client config.

    The server is then registered, over stdio, with every supported client found on PATH (or the ones named by
    -Clients): Claude Code, Codex CLI, GitHub Copilot CLI, and VS Code (GitHub Copilot Chat).

.EXAMPLE
    irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1))) -Clients claude,vscode

.EXAMPLE
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Release tag to install, for example v0.2.0. Defaults to the newest release, including pre-releases.
    [string] $Version,

    # Clients to register with: claude, codex, copilot, vscode, all, or none. Defaults to every one found on PATH.
    [string[]] $Clients,

    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\teamswork-ticketing-mcp'),

    # Don't prompt for the API key and account; keep whatever the secrets file already has.
    [switch] $SkipSecrets,

    # Unregister from the clients and delete the install folder. Add -RemoveSecrets to delete the secrets file too.
    [switch] $Uninstall,
    [switch] $RemoveSecrets
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar in 5.1

$Repo = 'joelst/teamswork-ticketing-mcp'
$ServerName = 'teamswork-ticketing'
$ExeName = 'TeamsWork.Ticketing.Mcp.exe'
$SecretsPath = Join-Path $env:APPDATA 'Microsoft\UserSecrets\teamswork-taas-mcp\secrets.json'
$AllClients = 'claude', 'codex', 'copilot', 'vscode'
$ClientCommand = @{ claude = 'claude'; codex = 'codex'; copilot = 'copilot'; vscode = 'code' }
$ExePath = Join-Path $InstallDir $ExeName

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Resolve-Clients {
    if (-not $Clients) {
        $found = $AllClients | Where-Object { Get-Command $ClientCommand[$_] -ErrorAction SilentlyContinue }
        if (-not $found) { Write-Host 'No supported MCP client found on PATH; skipping registration.' }
        return @($found)
    }
    $names = @($Clients | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    if ($names -contains 'none') { return @() }
    if ($names -contains 'all') { return $AllClients }
    $unknown = $names | Where-Object { $_ -notin $AllClients }
    if ($unknown) { throw "Unknown client(s): $($unknown -join ', '). Use $($AllClients -join ', '), all, or none." }
    return $names
}

# Runs a client CLI, returning $true on exit code 0. Output is shown only when it fails.
function Invoke-Client([string] $Command, [string[]] $Arguments) {
    # Windows PowerShell 5.1 turns a native command's stderr into terminating errors under 'Stop'.
    $ErrorActionPreference = 'Continue'
    $output = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host "    $_" }
        return $false
    }
    return $true
}

function Unregister-Client([string] $Client) {
    # Removing a server that isn't registered fails harmlessly, so the output and result are ignored.
    $ErrorActionPreference = 'Continue'
    switch ($Client) {
        'claude'  { claude mcp remove --scope user $ServerName 2>&1 | Out-Null }
        'codex'   { codex mcp remove $ServerName 2>&1 | Out-Null }
        'copilot' { copilot mcp remove $ServerName 2>&1 | Out-Null }
    }
}

function Register-Client([string] $Client) {
    if (-not (Get-Command $ClientCommand[$Client] -ErrorAction SilentlyContinue)) {
        Write-Warning "$Client`: '$($ClientCommand[$Client])' is not on PATH; skipped."
        return
    }
    Unregister-Client $Client
    $ok = switch ($Client) {
        'claude'  { Invoke-Client 'claude' @('mcp', 'add', '--transport', 'stdio', '--scope', 'user', $ServerName, '--', $ExePath, '--stdio') }
        'codex'   { Invoke-Client 'codex' @('mcp', 'add', $ServerName, '--', $ExePath, '--stdio') }
        'copilot' { Invoke-Client 'copilot' @('mcp', 'add', $ServerName, '--', $ExePath, '--stdio') }
        'vscode'  {
            # code is a .cmd wrapper, so the JSON goes through cmd.exe: escape its quotes explicitly. It exits 0 even
            # when it rejects the argument, so success is read from its output. Re-adding replaces the entry.
            $json = @{ name = $ServerName; type = 'stdio'; command = $ExePath; args = @('--stdio') } | ConvertTo-Json -Compress
            $escaped = $json.Replace('"', '\"')
            $ErrorActionPreference = 'Continue'
            $output = cmd.exe /d /c "code --add-mcp `"$escaped`"" 2>&1
            $added = ($output -join "`n") -match 'Added MCP servers'
            if (-not $added) { $output | ForEach-Object { Write-Host "    $_" } }
            $added
        }
    }
    if ($ok) { Write-Host "    registered with $Client" } else { Write-Warning "$Client`: registration failed (output above)." }
}

function Get-Release {
    $headers = @{ 'User-Agent' = 'teamswork-ticketing-mcp-installer'; Accept = 'application/vnd.github+json' }
    if ($Version) {
        $tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        return Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$Repo/releases/tags/$tag"
    }
    # /releases/latest skips pre-releases, and 0.x versions are published as pre-releases.
    # Windows PowerShell 5.1 returns a JSON array as one object, so index it rather than piping it.
    $release = @(Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$Repo/releases?per_page=1")[0]
    if (-not $release){ throw "No releases found for $Repo." }
    return $release
}

function Get-Asset($Release, [string] $Name) {
    $asset = $Release.assets | Where-Object name -EQ $Name | Select-Object -First 1
    if (-not $asset) { throw "Release $($Release.tag_name) has no file named $Name." }
    return $asset.browser_download_url
}

function Test-FileLocked([string] $Path) {
    if (-not (Test-Path $Path)) { return $false }
    try { [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None').Dispose(); return $false } catch { return $true }
}

function Install-Binary {
    $release = Get-Release
    $versionNumber = $release.tag_name.TrimStart('v')
    $archiveName = "teamswork-ticketing-mcp-$versionNumber-win-x64.zip"
    Write-Step "Downloading $archiveName ($($release.tag_name))"

    $temp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $archive = Join-Path $temp $archiveName
        Invoke-WebRequest -UseBasicParsing -Uri (Get-Asset $release $archiveName) -OutFile $archive
        $sums = (Invoke-WebRequest -UseBasicParsing -Uri (Get-Asset $release 'SHA256SUMS.txt')).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }

        $expected = $null
        foreach ($entry in $sums -split "`n") {
            if ($entry -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($archiveName))\s*$") { $expected = $Matches[1]; break }
        }
        if (-not $expected){ throw "SHA256SUMS.txt has no entry for $archiveName." }
        $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash
        if ($actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $archiveName (expected $expected, got $actual)." }
        Write-Host '    checksum OK'

        Expand-Archive -Path $archive -DestinationPath $temp
        $extracted = Get-ChildItem -Path $temp -Recurse -Filter $ExeName | Select-Object -First 1
        if (-not $extracted) { throw "$ExeName not found in $archiveName." }

        if (Test-FileLocked $ExePath) {
            throw "$ExePath is in use. Close the MCP clients that run it (Claude Code, Codex, Copilot, VS Code), then run the installer again."
        }
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Copy-Item -Force $extracted.FullName $ExePath
        Get-ChildItem -Path $extracted.DirectoryName -File | Where-Object Name -In 'LICENSE', 'README.md' |
            Copy-Item -Destination $InstallDir -Force
        Set-Content -Path (Join-Path $InstallDir 'version.txt') -Value $release.tag_name
        Unblock-File -Path $ExePath
        Write-Host "    installed $ExePath"

        $signature = Get-AuthenticodeSignature $ExePath
        if ($signature.Status -ne 'Valid') { Write-Warning "The executable's Authenticode signature is $($signature.Status)." }
    }
    finally {
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }
}

# Windows PowerShell 5.1 has no ?? operator.
function Get-First { $args | Where-Object { $_ } | Select-Object -First 1 }

function Read-Value([string] $Prompt, [string] $Current) {
    $suffix = if ($Current) { " [$Current]" } else { '' }
    while ($true) {
        $value = Read-Host "$Prompt$suffix"
        if ($value) { return $value.Trim() }
        if ($Current) { return $Current }
    }
}

function Set-Secrets {
    Write-Step "Configuring $SecretsPath"
    $secrets = [ordered]@{}
    if (Test-Path $SecretsPath) {
        # Windows PowerShell 5.1 has no ConvertFrom-Json -AsHashtable, so copy the properties across.
        $existing = Get-Content -Raw $SecretsPath | ConvertFrom-Json
        if ($existing) { $existing.PSObject.Properties | ForEach-Object { $secrets[$_.Name] = $_.Value } }
    }

    # Offer the signed-in Azure CLI account as the default identity, when there is one.
    $signedIn = $null
    if (-not $secrets['Ticketing:ServiceAccount:Id'] -and (Get-Command az -ErrorAction SilentlyContinue)) {
        $ErrorActionPreference = 'Continue'
        $json = az ad signed-in-user show --query '{id:id,name:displayName,mail:mail,upn:userPrincipalName}' -o json 2>$null
        if ($LASTEXITCODE -eq 0 -and $json) { $signedIn = ($json -join "`n") | ConvertFrom-Json }
        $ErrorActionPreference = 'Stop'
    }

    $hasKey = [bool]$secrets['Ticketing:ApiKey']
    $keyPrompt = if ($hasKey) { 'Ticketing API key (Enter keeps the current key)' } else { 'Ticketing API key (Ticketing app > Settings > API)' }
    while ($true) {
        $secure = Read-Host $keyPrompt -AsSecureString
        $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
        if ($key) { $secrets['Ticketing:ApiKey'] = $key.Trim(); break }
        if ($hasKey) { break }
    }

    Write-Host 'Ticket changes are recorded under this account (use your own):'
    $email = if ($signedIn.mail) { $signedIn.mail } else { $signedIn.upn }
    $secrets['Ticketing:ServiceAccount:Id'] = Read-Value '  Entra object ID' (Get-First $secrets['Ticketing:ServiceAccount:Id'] $signedIn.id)
    $secrets['Ticketing:ServiceAccount:Name'] = Read-Value '  Display name' (Get-First $secrets['Ticketing:ServiceAccount:Name'] $signedIn.name)
    $secrets['Ticketing:ServiceAccount:Email'] = Read-Value '  Email' (Get-First $secrets['Ticketing:ServiceAccount:Email'] $email)

    New-Item -ItemType Directory -Force -Path (Split-Path $SecretsPath) | Out-Null
    $secrets | ConvertTo-Json | Set-Content -Encoding UTF8 -Path $SecretsPath
    Write-Host "    saved (the API key is stored only in this file)"
}

# Sends an MCP initialize request over stdio and checks for a response, the same smoke test the release runs.
function Test-Server {
    Write-Step 'Checking that the server starts'
    $psi = [Diagnostics.ProcessStartInfo]::new($ExePath, '--stdio')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($psi)
    try {
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"installer","version":"1"}}}')
        $process.StandardInput.Flush()
        $line = $process.StandardOutput.ReadLineAsync()
        if ($line.Wait(20000) -and $line.Result -match '"serverInfo"') {
            Write-Host '    OK'
            return $true
        }
        if (-not $process.HasExited) { $process.Kill() }
        $null = $process.WaitForExit(5000)
        Write-Warning "The server did not start. Its output:`n$($stderr.Result)"
        return $false
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    # Windows PowerShell 5.1 may default to TLS 1.0/1.1, which GitHub rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw 'This installer is for Windows. On macOS and Linux use scripts/install.sh.'
}

$targets = Resolve-Clients

if ($Uninstall) {
    Write-Step 'Unregistering'
    foreach ($client in $targets) {
        if ($client -eq 'vscode') {
            Write-Host "    vscode: run 'MCP: Open User Configuration' and delete the '$ServerName' entry"
        }
        elseif (Get-Command $ClientCommand[$client] -ErrorAction SilentlyContinue) {
            Unregister-Client $client
            Write-Host "    removed from $client"
        }
    }
    if (Test-FileLocked $ExePath) { throw "$ExePath is in use. Close the MCP clients that run it, then run again." }
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Write-Host "    deleted $InstallDir"
    if ($RemoveSecrets) {
        Remove-Item -Force $SecretsPath -ErrorAction SilentlyContinue
        Write-Host "    deleted $SecretsPath"
    }
    return
}

Install-Binary
if (-not $SkipSecrets) { Set-Secrets }
elseif (-not (Test-Path $SecretsPath)) { Write-Warning "No secrets file at $SecretsPath; the server needs its settings in environment variables instead." }
$started = Test-Server

if ($targets) {
    Write-Step 'Registering with MCP clients'
    foreach ($client in $targets) { Register-Client $client }
}

Write-Host ''
if (-not $started) {
    Write-Host "Installed, but the server can't start yet. Fix the settings above (or run the installer again without"
    Write-Host '-SkipSecrets), then restart your MCP client.'
    return
}
Write-Host "Done. Restart your MCP client and look for '$ServerName' (12 tools)."
Write-Host 'Run the installer again to upgrade; client configurations do not need to change.'
