<#
.SYNOPSIS
    Installs the TeamsWork Ticketing MCP server on Windows and registers it with local MCP clients.

.DESCRIPTION
    Downloads a release from GitHub, checks it against the release's SHA256SUMS.txt and its Authenticode signature,
    and puts the executable at a fixed per-user path (default %LOCALAPPDATA%\Programs\teamswork-ticketing-mcp), so
    client configurations keep working across upgrades. Run it again to upgrade; running clients keep the old version
    until they restart.

    The server needs the Ticketing API key and the account that ticket changes are attributed to. Both are stored in
    the .NET user-secrets file the server reads (%APPDATA%\Microsoft\UserSecrets\teamswork-taas-mcp\secrets.json),
    never in a client config.

    The server is then registered, over stdio, with every supported client found on PATH (or the ones named by
    -Clients): Claude Code, Codex CLI, GitHub Copilot CLI, and VS Code (GitHub Copilot Chat).

    Parameters:
      -Version <tag>    Release to install, for example v0.2.0, or 'latest'. Defaults to the release this copy of the
                        script was attached to, or for the copy on main, the newest release (pre-releases included).
      -Clients <list>   claude, codex, copilot, vscode, all, or none. Defaults to every one found on PATH.
      -InstallDir <dir> Install somewhere else.
      -SkipSecrets      Don't prompt for the API key and account; keep whatever the secrets file already has.
      -Uninstall        Unregister from the clients and delete the server's files (and the folder, if empty).
      -RemoveSecrets    With -Uninstall, also delete the secrets file.

.EXAMPLE
    irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.ps1))) -Clients claude,vscode

.EXAMPLE
    .\install.ps1 -Uninstall
#>

# Everything runs inside this script block so that `irm | iex`, which runs a script in the caller's own scope, leaves
# nothing behind in the user's session: not the preferences set below, not the functions, and not the parameters
# (which would otherwise overwrite any $Version or $Clients variable the user already has). -File and
# [scriptblock]::Create() invocations pass their arguments through @args.
& {
    [CmdletBinding()]
    param(
        [string] $Version,
        [string[]] $Clients,
        [string] $InstallDir,
        [switch] $SkipSecrets,
        [switch] $Uninstall,
        [switch] $RemoveSecrets
    )

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar in 5.1

    if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
        throw 'This installer is for Windows. On macOS and Linux use scripts/install.sh.'
    }
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        # Windows PowerShell 5.1 may default to TLS 1.0/1.1, which GitHub rejects.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    # The release workflow sets this in the copy attached to each release, so that copy installs its own release.
    $PinnedTag = ''

    $Repo = 'joelst/teamswork-ticketing-mcp'
    $ServerName = 'teamswork-ticketing'
    $ExeName = 'TeamsWork.Ticketing.Mcp.exe'
    $SecretsPath = Join-Path $env:APPDATA 'Microsoft\UserSecrets\teamswork-taas-mcp\secrets.json'
    $AllClients = 'claude', 'codex', 'copilot', 'vscode'
    $ClientCommand = @{ claude = 'claude'; codex = 'codex'; copilot = 'copilot'; vscode = 'code' }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\teamswork-ticketing-mcp' }
    $ExePath = Join-Path $InstallDir $ExeName
    $VersionPath = Join-Path $InstallDir 'TeamsWork.Ticketing.Mcp.version'
    # The only other files the installer creates, so cleanup in a shared -InstallDir never matches anyone else's
    # (such as a TeamsWork.Ticketing.Mcp.exe.config): the staged copy, and old copies renamed aside during upgrades.
    $OwnedFilePattern = '^' + [regex]::Escape($ExeName) + '\.(new|[0-9a-f]{32}\.old)$'
    # Warnings repeated at the end, where they won't scroll out of sight.
    $Notices = [Collections.Generic.List[string]]::new()

    function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

    function Add-Notice([string] $Message) {
        Write-Warning $Message
        $Notices.Add($Message)
    }

    function Get-OwnedFiles {
        Get-ChildItem -Path $InstallDir -File -ErrorAction SilentlyContinue | Where-Object Name -Match $OwnedFilePattern
    }

    # The client's executable or .cmd wrapper. npm also installs a .ps1 shim for its CLIs, which PowerShell would
    # otherwise prefer and which the execution policy blocks on a default Windows PowerShell 5.1 (Restricted).
    function Find-Client([string] $Client) {
        $command = Get-Command $ClientCommand[$Client] -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $command.Source }
    }

    function Resolve-Clients {
        if (-not $Clients) {
            $found = @($AllClients | Where-Object { Find-Client $_ })
            if (-not $found) { Write-Host 'No supported MCP client found on PATH; skipping registration.' }
            return $found
        }
        $names = @($Clients | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
        if ($names -contains 'none') { return @() }
        if ($names -contains 'all') { return $AllClients }
        $unknown = $names | Where-Object { $_ -notin $AllClients }
        if ($unknown) { throw "Unknown client(s): $($unknown -join ', '). Use $($AllClients -join ', '), all, or none." }
        return $names
    }

    # Runs a client CLI and returns its exit code and combined output.
    function Invoke-Client([string] $Client, [string[]] $Arguments) {
        # Windows PowerShell 5.1 turns a native command's stderr into terminating errors under 'Stop'.
        $ErrorActionPreference = 'Continue'
        if ($Client -eq 'vscode') {
            # code is a .cmd wrapper, so arguments go through cmd.exe, which needs the JSON's quotes escaped. Each \"
            # also toggles cmd's own quoting, leaving parts of the path unquoted, so characters cmd acts on there (&, ^,
            # %, and so on; & is legal in a Windows user name) are written as JSON \u escapes. The command line must
            # not start with a quote: cmd /c would strip it and the last one.
            $line = ($Arguments | ForEach-Object {
                    $arg = [regex]::Replace($_, '[&^%|<>!()]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
                    '"' + $arg.Replace('"', '\"') + '"'
                }) -join ' '
            $output = cmd.exe /d /c "code $line" 2>&1
        }
        else {
            $output = & (Find-Client $Client) @Arguments 2>&1
        }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
    }

    function Unregister-Client([string] $Client) {
        # Removing a server that isn't registered fails harmlessly, so the result is ignored.
        $null = switch ($Client) {
            'claude'  { Invoke-Client $Client @('mcp', 'remove', '--scope', 'user', $ServerName) }
            'codex'   { Invoke-Client $Client @('mcp', 'remove', $ServerName) }
            'copilot' { Invoke-Client $Client @('mcp', 'remove', $ServerName) }
        }
    }

    function Register-Client([string] $Client) {
        if (-not (Find-Client $Client)) {
            Write-Warning "$Client`: '$($ClientCommand[$Client])' is not on PATH; skipped."
            return
        }
        Unregister-Client $Client
        $result = switch ($Client) {
            'claude'  { Invoke-Client $Client @('mcp', 'add', '--transport', 'stdio', '--scope', 'user', $ServerName, '--', $ExePath, '--stdio') }
            'codex'   { Invoke-Client $Client @('mcp', 'add', $ServerName, '--', $ExePath, '--stdio') }
            'copilot' { Invoke-Client $Client @('mcp', 'add', $ServerName, '--', $ExePath, '--stdio') }
            'vscode'  {
                $json = @{ name = $ServerName; type = 'stdio'; command = $ExePath; args = @('--stdio') } | ConvertTo-Json -Compress
                Invoke-Client $Client @('--add-mcp', $json)
            }
        }
        # code exits 0 even when it rejects the argument, so its success is read from the output. Re-adding replaces
        # the entry.
        $ok = if ($Client -eq 'vscode') { ($result.Output -join "`n") -match 'Added MCP servers' } else { $result.ExitCode -eq 0 }
        if ($ok) {
            Write-Host "    registered with $Client"
        }
        else {
            $result.Output | ForEach-Object { Write-Host "    $_" }
            Write-Warning "$Client`: registration failed (output above)."
        }
    }

    function Get-Asset($Release, [string] $Name) {
        $Release.assets | Where-Object name -EQ $Name | Select-Object -First 1
    }

    function Get-ArchiveName($Release) { "teamswork-ticketing-mcp-$($Release.tag_name.TrimStart('v'))-win-x64.zip" }

    # A release is usable once the workflow has attached its files, which can be some minutes after it is published.
    function Test-ReleaseReady($Release) {
        (Get-Asset $Release (Get-ArchiveName $Release)) -and (Get-Asset $Release 'SHA256SUMS.txt')
    }

    function Invoke-GitHubApi([string] $Uri) {
        $headers = @{ 'User-Agent' = 'teamswork-ticketing-mcp-installer'; Accept = 'application/vnd.github+json' }
        # Unauthenticated API calls are limited to 60 an hour per IP address, which shared networks and CI runners
        # hit, so use a token when there is one. The repository is public, so if the token is rejected (expired, or
        # scoped to another organization), try again without it.
        if ($env:GITHUB_TOKEN) {
            try { return Invoke-RestMethod -Headers ($headers + @{ Authorization = "Bearer $env:GITHUB_TOKEN" }) $Uri }
            catch { if ($_.Exception.Response.StatusCode -notin 401, 403) { throw } }
        }
        Invoke-RestMethod -Headers $headers $Uri
    }

    function Get-Release {
        $api = "https://api.github.com/repos/$Repo/releases"
        $tag = if ($Version -and $Version -ne 'latest') { $Version } elseif (-not $Version) { $PinnedTag } else { '' }
        if ($tag) {
            if (-not $tag.StartsWith('v')) { $tag = "v$tag" }
            try { $release = Invoke-GitHubApi "$api/tags/$tag" }
            catch { throw "Couldn't get release $tag of $Repo ($($_.Exception.Message)). Check the tag on https://github.com/$Repo/releases." }
            if (-not (Test-ReleaseReady $release)) { throw "Release $tag has no $(Get-ArchiveName $release) or SHA256SUMS.txt (yet)." }
            return $release
        }
        # Newest first. /releases/latest would skip pre-releases, and 0.x versions are published as pre-releases.
        # Assigning, rather than piping, keeps Windows PowerShell 5.1 from treating the JSON array as one object.
        $releases = Invoke-GitHubApi "$($api)?per_page=20"
        foreach ($release in $releases) {
            # A token with push access also lists drafts, whose files can't be downloaded without it.
            if (-not $release.draft -and (Test-ReleaseReady $release)) { return $release }
        }
        throw "No release of $Repo has a Windows build yet."
    }

    function Install-Binary {
        $release = Get-Release
        $archiveName = Get-ArchiveName $release
        Write-Step "Downloading $archiveName ($($release.tag_name))"

        $temp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
        New-Item -ItemType Directory -Path $temp | Out-Null
        try {
            $archive = Join-Path $temp $archiveName
            Invoke-WebRequest -UseBasicParsing -Uri (Get-Asset $release $archiveName).browser_download_url -OutFile $archive
            $sumsFile = Join-Path $temp 'SHA256SUMS.txt'
            Invoke-WebRequest -UseBasicParsing -Uri (Get-Asset $release 'SHA256SUMS.txt').browser_download_url -OutFile $sumsFile

            $expected = $null
            foreach ($entry in Get-Content $sumsFile) {
                if ($entry -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($archiveName))\s*$") { $expected = $Matches[1]; break }
            }
            if (-not $expected) { throw "SHA256SUMS.txt has no entry for $archiveName." }
            $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash
            if ($actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $archiveName (expected $expected, got $actual)." }

            Expand-Archive -Path $archive -DestinationPath $temp
            $extracted = Get-ChildItem -Path $temp -Recurse -Filter $ExeName | Select-Object -First 1
            if (-not $extracted) { throw "$ExeName not found in $archiveName." }

            # The checksum file comes from the same release, so it only proves the download is intact. A valid signature
            # also proves the file was signed with a trusted code-signing certificate and not changed since; the signer
            # is printed so it can be checked.
            $signature = Get-AuthenticodeSignature $extracted.FullName
            if ($signature.Status -ne 'Valid') {
                throw "The executable's Authenticode signature is $($signature.Status): $($signature.StatusMessage) Not installing it."
            }
            $signer = $signature.SignerCertificate.Subject
            Write-Host "    checksum and signature OK ($signer)"

            # An upgrade should come from the same publisher as the copy it replaces. Signing certificates are
            # reissued often, so the subject is compared, not the thumbprint.
            if (Test-Path $ExePath) {
                $previous = Get-AuthenticodeSignature $ExePath
                if ($previous.Status -eq 'Valid' -and $previous.SignerCertificate.Subject -ne $signer) {
                    Add-Notice ("The new executable is signed by a different publisher than the one it replaces.`n" +
                        "    before: $($previous.SignerCertificate.Subject)`n    now:    $signer`n" +
                        "Check that this change is expected, for example in the release notes at https://github.com/$Repo/releases.")
                }
                elseif ($previous.Status -notin 'Valid', 'NotSigned') {
                    Add-Notice "The installed executable's signature is $($previous.Status), so its publisher couldn't be compared with the new one ($signer)."
                }
            }

            New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
            # A running executable can't be overwritten or deleted, but it can be renamed. Stage the new file next to
            # the old one, move the old one aside, and move the new one in, so clients don't have to be closed first.
            # They keep running the old version until they restart; its renamed file is deleted on a later run.
            Get-OwnedFiles | Remove-Item -Force -ErrorAction SilentlyContinue
            $staged = "$ExePath.new"
            Copy-Item -Force $extracted.FullName $staged
            Unblock-File -Path $staged
            $old = $null
            if (Test-Path $ExePath) {
                $old = "$ExePath.$([guid]::NewGuid().ToString('N')).old"
                Move-Item -Path $ExePath -Destination $old
            }
            try {
                Move-Item -Path $staged -Destination $ExePath
            }
            catch {
                if ($old) { Move-Item -Path $old -Destination $ExePath }
                throw
            }
            if ($old) { Remove-Item -Force $old -ErrorAction SilentlyContinue }

            # Only files named after the executable, so a shared -InstallDir (such as a tools folder on PATH) gets
            # nothing that could clash with other programs' files, and uninstall knows exactly what to delete.
            Set-Content -Path $VersionPath -Value $release.tag_name
            Write-Host "    installed $ExePath"
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

    function Read-Secret([string] $Prompt) {
        $secure = Read-Host $Prompt -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    function Set-Secrets {
        Write-Step "Configuring $SecretsPath"
        $secrets = [ordered]@{}
        if (Test-Path $SecretsPath) {
            try { $existing = Get-Content -Raw $SecretsPath | ConvertFrom-Json }
            catch { throw "$SecretsPath is not valid JSON ($($_.Exception.Message)). Fix or delete it, then run the installer again." }
            # A nested object ("Ticketing": { ... }) would sit beside the flat keys written below, and .NET refuses to
            # load a file where both forms name the same setting.
            if ($existing -and ($existing.PSObject.Properties.Value | Where-Object { $_ -is [pscustomobject] -or $_ -is [array] })) {
                throw "$SecretsPath uses nested objects. Rewrite it with flat ""Ticketing:..."" keys (see docs/stdio.md), or run again with -SkipSecrets."
            }
            # Windows PowerShell 5.1 has no ConvertFrom-Json -AsHashtable, so copy the properties across. [ordered]
            # keys are case-insensitive, like .NET configuration keys.
            if ($existing) { $existing.PSObject.Properties | ForEach-Object { $secrets[$_.Name] = $_.Value } }
        }

        # Offer the signed-in Azure CLI account as the default identity, when there is one.
        $signedIn = $null
        $az = Get-Command az -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $secrets['Ticketing:ServiceAccount:Id'] -and $az) {
            $ErrorActionPreference = 'Continue'
            $json = & $az.Source ad signed-in-user show --query '{id:id,name:displayName,email:mail || userPrincipalName}' -o json 2>$null
            if ($LASTEXITCODE -eq 0 -and $json) { $signedIn = ($json -join "`n") | ConvertFrom-Json }
            $ErrorActionPreference = 'Stop'
        }

        $hasKey = [bool]$secrets['Ticketing:ApiKey']
        $keyPrompt = if ($hasKey) { 'Ticketing API key (Enter keeps the current key)' } else { 'Ticketing API key (Ticketing app > Settings > API)' }
        while ($true) {
            $key = Read-Secret $keyPrompt
            if ($key) { $secrets['Ticketing:ApiKey'] = $key.Trim(); break }
            if ($hasKey) { break }
        }

        Write-Host 'Ticket changes are attributed to this account (use your own):'
        $secrets['Ticketing:ServiceAccount:Id'] = Read-Value '  Entra object ID' (Get-First $secrets['Ticketing:ServiceAccount:Id'] $signedIn.id)
        $secrets['Ticketing:ServiceAccount:Name'] = Read-Value '  Display name' (Get-First $secrets['Ticketing:ServiceAccount:Name'] $signedIn.name)
        $secrets['Ticketing:ServiceAccount:Email'] = Read-Value '  Email' (Get-First $secrets['Ticketing:ServiceAccount:Email'] $signedIn.email)

        New-Item -ItemType Directory -Force -Path (Split-Path $SecretsPath) | Out-Null
        $secrets | ConvertTo-Json | Set-Content -Encoding UTF8 -Path $SecretsPath
        Write-Host '    saved (the API key is stored only in this file)'
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
            # Wait for the exit, so the executable isn't still locked when the caller moves on (for example to uninstall).
            if (-not $process.HasExited) { $process.Kill() }
            $null = $process.WaitForExit(5000)
            $process.Dispose()
        }
    }

    $targets = Resolve-Clients

    if ($Uninstall) {
        Write-Step 'Unregistering'
        foreach ($client in $targets) {
            if ($client -eq 'vscode') {
                Write-Host "    vscode: run 'MCP: Open User Configuration' and delete the '$ServerName' entry"
            }
            elseif (Find-Client $client) {
                Unregister-Client $client
                Write-Host "    removed from $client"
            }
        }
        # Only the installer's own files: -InstallDir may be a folder shared with other programs.
        if (Test-Path $ExePath) {
            try { Remove-Item -Force $ExePath }
            catch { throw "Couldn't delete $ExePath because a client is still running the server. Quit the MCP clients, then run the uninstall again." }
        }
        Get-OwnedFiles | Remove-Item -Force -ErrorAction SilentlyContinue
        Remove-Item -Force $VersionPath -ErrorAction SilentlyContinue
        # After an upgrade, a client that hasn't restarted is running the renamed old copy, which can't be deleted yet.
        $locked = @(Get-OwnedFiles)
        if ($locked) {
            throw "Couldn't delete $($locked.FullName -join ', ') because a client is still running it. Quit the MCP clients, then run the uninstall again."
        }
        if ((Test-Path $InstallDir) -and -not (Get-ChildItem -Force $InstallDir)) { Remove-Item -Force $InstallDir }
        Write-Host "    removed the server from $InstallDir"
        if ($RemoveSecrets) {
            Remove-Item -Force $SecretsPath -ErrorAction SilentlyContinue
            Write-Host "    deleted $SecretsPath"
        }
        return
    }

    Install-Binary
    if (-not $SkipSecrets) { Set-Secrets }
    elseif (-not (Test-Path $SecretsPath)) { Write-Warning "No secrets file at $SecretsPath; the server needs its settings in environment variables instead, set in each client's MCP config (see docs/stdio.md)." }
    # Environment variables override the secrets file. Whether one set here reaches the server depends on the client:
    # Claude Code passes its environment on, GitHub Copilot CLI and Codex pass only a few variables. The startup check
    # below sees them either way, so it can pass where a client would not. Names only: the values may be secrets.
    $overrides = @(Get-ChildItem Env: | Where-Object Name -Like 'Ticketing__*' | ForEach-Object Name | Sort-Object)
    if ($overrides) {
        Add-Notice ("These environment variables are set: $($overrides -join ', '). They override the secrets file in " +
            "clients that pass their environment to the server, such as Claude Code, but GitHub Copilot CLI and Codex " +
            "don't pass them. Remove them if the secrets file should be used.")
    }
    $started = Test-Server

    if ($targets) {
        Write-Step 'Registering with MCP clients'
        foreach ($client in $targets) { Register-Client $client }
    }

    Write-Host ''
    foreach ($notice in $Notices) { Write-Warning $notice }
    if (-not $started) {
        Write-Host "Installed, but the server can't start yet. Fix the settings above (or run the installer again without"
        Write-Host '-SkipSecrets), then restart your MCP client.'
        return
    }
    Write-Host "Done. Restart your MCP client and look for '$ServerName' (12 tools)."
    Write-Host 'Run the installer again to upgrade; client configurations do not need to change.'
} @args
