<#
.SYNOPSIS
    Regenerates plugin/cowork/tools/teamswork-ticketing-tools.json from the server's own tools/list.

.DESCRIPTION
    The Cowork manifest's connector must point at a tool-description file (the v1.28 schema requires it), though
    Cowork itself reads the tools from the endpoint's tools/list. This builds the server, starts it once over stdio
    with placeholder settings, asks for tools/list, and writes the result. No upload folder is set, so the stdio-only
    upload_ticket_files tool is left out, as it is on the hosted endpoint. Nothing is sent to the Ticketing API.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/TeamsWork.Ticketing.Mcp/TeamsWork.Ticketing.Mcp.csproj'
$outDir = Join-Path $root 'artifacts/tools-list'

dotnet publish $project -c Release -o $outDir -p:UseAppHost=false -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$(Join-Path $outDir 'TeamsWork.Ticketing.Mcp.dll')`" --stdio")
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
# Placeholders only: listing tools makes no API call, and the server needs these to start. Any Ticketing setting in
# this shell's environment is removed so it can't add or hide a tool.
foreach ($name in @($psi.Environment.Keys)) { if ($name -like 'Ticketing*') { [void]$psi.Environment.Remove($name) } }
$psi.Environment['Ticketing__ApiKey'] = 'placeholder-for-tools-list'
$psi.Environment['Ticketing__ServiceAccount__Id'] = '00000000-0000-0000-0000-0000000000c1'
$psi.Environment['Ticketing__ServiceAccount__Name'] = 'Tools List'
$psi.Environment['Ticketing__ServiceAccount__Email'] = 'tools-list@example.test'

$process = [System.Diagnostics.Process]::Start($psi)
try {
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"update-cowork-tool-description","version":"1"}}}')
    $null = $process.StandardOutput.ReadLine()
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $reply = $process.StandardOutput.ReadLine() | ConvertFrom-Json
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { $process.Kill() }
}
if (-not $reply.result.tools) { throw "tools/list returned no tools: $($reply | ConvertTo-Json -Depth 5 -Compress)" }

$target = Join-Path $root 'plugin/cowork/tools/teamswork-ticketing-tools.json'
$json = $reply.result | ConvertTo-Json -Depth 50
[IO.File]::WriteAllText($target, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($reply.result.tools.Count) tools to $target"
