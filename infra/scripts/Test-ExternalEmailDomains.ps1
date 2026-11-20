<#
.SYNOPSIS
    Checks a Ticketing:ExternalEmailDomains value the way the server does, before anything is deployed.

.DESCRIPTION
    The server refuses to start with Entra sign-in unless the value names at least one email domain, or is * alone.
    Bicep can only check that the value isn't empty, so a value such as "," or "@ ;" would deploy a revision that never
    starts. The deployment entry points (the pipeline's Core and Deploy stages, Deploy-TestInstance.ps1) run this first.

    Entries are separated by commas, semicolons or spaces, and a leading @ is ignored, as the server reads them. Each must
    be a plain ASCII host name (punycode for an internationalised one), or the whole value must be * alone.

    Throws with a message naming what to set when the value won't do; returns nothing when it will.

.EXAMPLE
    ./infra/scripts/Test-ExternalEmailDomains.ps1 -Value 'contoso.com, contoso.co.uk'
#>
param(
    [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] [string] $Value
)

$entries = @("$Value" -split '[,; ]' | ForEach-Object { $_.Trim().TrimStart('@') } | Where-Object { $_ })

# The server's host-name rule (ToolValidation.IsHostName): two or more labels of letters, digits and hyphens, none
# starting or ending with a hyphen, and a last label that isn't all digits.
function Test-HostName([string] $Domain) {
    if ($Domain.Length -lt 1 -or $Domain.Length -gt 253) { return $false }
    $labels = $Domain.Split('.')
    if ($labels.Count -lt 2 -or $labels[-1] -match '^[0-9]+$') { return $false }
    foreach ($label in $labels) {
        if ($label -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$') { return $false }
    }
    return $true
}

if ($entries.Count -eq 1 -and $entries[0] -eq '*') { return }

$invalid = @($entries | Where-Object { -not (Test-HostName $_) })
if ($entries.Count -eq 0 -or $invalid.Count -gt 0) {
    $what = if ($entries.Count -eq 0) { 'names no domain' } else { "has entries that aren't plain domain names ($($invalid -join ', '))" }
    throw "externalEmailDomains $what. Set it to your organization's email domains, such as contoso.com, separated by commas, or to * alone to allow any domain."
}
