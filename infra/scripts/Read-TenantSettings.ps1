<#
.SYNOPSIS
    Reads a deployment script's settings from the tenant settings file, infra/tenant.local.json (git-ignored).

.DESCRIPTION
    Keeps one tenant's values out of the repository and off the command line. The file holds an object per script,
    named after it, whose properties are that script's parameters:

      {
        "New-EntraAppRegistrations": { "TenantId": "<tenant ID>" },
        "Deploy-TestInstance": { "Location": "<region>", "ExternalEmailDomains": "contoso.com" }
      }

    Returns a hashtable of the values in the calling script's object for the parameters the caller didn't pass, so a
    value passed on the command line wins. The script assigns them to its parameter variables, which applies each
    parameter's type and validation as if it had been passed. A name the script has no parameter for is refused, so a
    misspelt setting stops the run rather than being left out unnoticed.

    Without -Path the default file is read when there is one; a -Path given must exist.

.EXAMPLE
    $fromFile = & (Join-Path $PSScriptRoot 'Read-TenantSettings.ps1') -Command $MyInvocation.MyCommand -Bound $PSBoundParameters -Path $SettingsPath
    foreach ($name in $fromFile.Keys) { Set-Variable -Name $name -Value $fromFile[$name] -WhatIf:$false -Confirm:$false }   # local, so -WhatIf mustn't skip it
#>
param(
    [Parameter(Mandatory)] [System.Management.Automation.CommandInfo] $Command,
    [Parameter(Mandatory)] [AllowEmptyCollection()] [System.Collections.IDictionary] $Bound,
    [string] $Path,
    # Parameters for one run, such as -SkipImage, which the file would otherwise apply to every run unnoticed.
    [string[]] $CommandLineOnly = @()
)

$ErrorActionPreference = 'Stop'
$section = [IO.Path]::GetFileNameWithoutExtension($Command.Name)

if ($Path) {
    # Resolved where PowerShell is, as the caller reads it, not against the process's directory.
    $Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The settings file $Path doesn't exist." }
}
else {
    $Path = Join-Path (Split-Path -Parent $PSScriptRoot) 'tenant.local.json'
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @{} }
}

$text = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path
try { $file = $text | ConvertFrom-Json }
catch { throw "$Path isn't valid JSON: $($_.Exception.Message)" }
# PowerShell 7 unrolls a top-level array, so one holding a single object would pass for that object without this.
if ($text.TrimStart().StartsWith('[') -or $file -isnot [System.Management.Automation.PSCustomObject]) { throw "$Path must hold a JSON object with a property per script, such as ""$section""." }
$saved = $file.PSObject.Properties[$section]
if (-not $saved -or $null -eq $saved.Value) { return @{} }
if ($saved.Value -isnot [System.Management.Automation.PSCustomObject]) { throw """$section"" in $Path must be an object of parameter names and values." }

# The parameters a file may set: not the common ones (-WhatIf, -Verbose, ...), nor the one naming the file.
$excluded = [System.Management.Automation.PSCmdlet]::CommonParameters + [System.Management.Automation.PSCmdlet]::OptionalCommonParameters + 'SettingsPath'
$names = @($Command.Parameters.Keys | Where-Object { $_ -notin $excluded })

$values = @{}
$unknown = @()
foreach ($property in $saved.Value.PSObject.Properties) {
    if ($property.Name -in $CommandLineOnly) { throw """$section"" in $Path sets $($property.Name), which is for one run: pass -$($property.Name) on the command line instead." }
    $name = $names | Where-Object { $_ -eq $property.Name } | Select-Object -First 1
    if (-not $name) { $unknown += $property.Name; continue }
    if (-not $Bound.ContainsKey($name)) { $values[$name] = $property.Value }
}
if ($unknown) { throw """$section"" in $Path has $($unknown -join ', '), which it takes no parameter for. It takes: $($names -join ', ')." }

if ($values.Count) { Write-Host "Using $(@($values.Keys | Sort-Object) -join ', ') from $Path." }
return $values
