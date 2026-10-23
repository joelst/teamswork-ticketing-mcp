$ErrorActionPreference = 'Stop'

$installer = Join-Path $PSScriptRoot '..\install.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $installer), [ref]$null, [ref]$null)
foreach ($name in 'Get-Registration', 'Register-Client', 'Unregister-Client') {
    $definition = $ast.Find(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name },
        $true
    )
    if (-not $definition) { throw "install.ps1 has no function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$oldAppData = $env:APPDATA
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "taas-client-registration-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $env:APPDATA = $testRoot
    $script:ServerName = 'teamswork-ticketing'
    $script:ExePath = Join-Path $testRoot 'Programs with spaces\TeamsWork.Ticketing.Mcp.exe'
    $script:ClientCommand = @{ claude = 'claude'; codex = 'codex'; copilot = 'copilot'; vscode = 'code' }
    $script:Registration = ''
    $script:Calls = @()
    $script:FailAdd = $false

    function Find-Client([string] $Client) { "C:\mock\$Client.exe" }

    function Invoke-Client([string] $Client, [string[]] $Arguments) {
        $script:Calls += [pscustomobject]@{ Client = $Client; Arguments = @($Arguments) }
        $joined = $Arguments -join ' '
        if ($joined -eq "mcp get $script:ServerName") {
            return [pscustomobject]@{ ExitCode = if ($script:Registration) { 0 } else { 1 }; Output = @($script:Registration) }
        }
        if ($joined -like 'mcp remove*') {
            $script:Registration = ''
            return [pscustomobject]@{ ExitCode = 0; Output = @() }
        }
        if ($joined -like 'mcp add*') {
            if (-not $script:FailAdd) { $script:Registration = $script:ExePath }
            return [pscustomobject]@{ ExitCode = if ($script:FailAdd) { 1 } else { 0 }; Output = @('mock output') }
        }
        if ($Client -eq 'vscode' -and $Arguments[0] -eq '--add-mcp') {
            return [pscustomobject]@{
                ExitCode = 0
                Output = @($(if ($script:FailAdd) { 'Rejected MCP server' } else { 'Added MCP servers' }))
            }
        }
        throw "Unexpected mock invocation: $Client $joined"
    }

    function Assert([string] $Message, [bool] $Condition) {
        if (-not $Condition) { throw "FAIL: $Message" }
        Write-Output "ok   $Message"
    }

    $script:Registration = $script:ExePath
    $script:Calls = @()
    Assert 'an existing Claude registration is preserved on upgrade' (Register-Client 'claude')
    Assert 'the existing registration does not get removed or rewritten' ($script:Calls.Count -eq 1)

    $script:Registration = 'C:\old\TeamsWork.Ticketing.Mcp.exe'
    $script:Calls = @()
    Assert 'a stale Codex registration is replaced' (Register-Client 'codex')
    Assert 'Codex receives the stdio command' (($script:Calls[-1].Arguments -join ' ') -eq "mcp add $script:ServerName -- $script:ExePath --stdio")

    $script:FailAdd = $true
    $script:Registration = ''
    $script:Calls = @()
    Assert 'a failed Copilot registration is reported as failure' (-not (Register-Client 'copilot'))
    $script:FailAdd = $false

    $vscodeFolder = Join-Path $testRoot 'Code\User'
    New-Item -ItemType Directory -Path $vscodeFolder -Force | Out-Null
    $script:Calls = @()
    Assert 'VS Code reports successful registration only after its confirmation' (Register-Client 'vscode')
    $vscodeJson = $script:Calls[-1].Arguments[1] | ConvertFrom-Json
    Assert 'VS Code receives a stdio entry with the installed executable and argument' (
        $vscodeJson.type -eq 'stdio' -and
        $vscodeJson.command -eq $script:ExePath -and
        $vscodeJson.args.Count -eq 1 -and
        $vscodeJson.args[0] -eq '--stdio'
    )

    $vscodeConfig = Join-Path $vscodeFolder 'mcp.json'
    Set-Content -LiteralPath $vscodeConfig -Value (@{ servers = @{ $script:ServerName = @{ command = $script:ExePath } } } | ConvertTo-Json -Depth 4) -Encoding UTF8
    $script:Calls = @()
    Assert 'an existing VS Code registration is left intact during upgrade' (Register-Client 'vscode')
    Assert 'the VS Code entry is not rewritten on upgrade' ($script:Calls.Count -eq 0)
}
finally {
    if ($null -eq $oldAppData) { Remove-Item Env:APPDATA -ErrorAction SilentlyContinue }
    else { $env:APPDATA = $oldAppData }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
