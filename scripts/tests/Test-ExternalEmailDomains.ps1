# Tests infra/scripts/Test-ExternalEmailDomains.ps1, the check the deployment entry points run before deploying: it
# must accept what the server accepts (TicketingOptions) and refuse what would stop the server starting. Exits 1 on any
# failure. Runs on Windows PowerShell 5.1 and 7.
$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot '../../infra/scripts/Test-ExternalEmailDomains.ps1'

$failures = 0
function Check([string] $what, [bool] $ok) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failures++ }
}

function Accepts([string] $value) {
    try { & $check -Value $value; return $true } catch { return $false }
}

foreach ($value in 'contoso.com', 'contoso.com, contoso.co.uk', '@contoso.com', 'contoso.com;fabrikam.com', ' * ', 'xn--bcher-kva.example') {
    Check "'$value' is accepted" (Accepts $value)
}

foreach ($value in '', ' ', ',', '@ ;', 'contoso.com, *', '*.contoso.com', 'contoso', 'contoso.123', '-contoso.com', 'bücher.example', 'contoso..com') {
    Check "'$value' is refused" (-not (Accepts $value))
}

# Two the server refuses that a case-insensitive or $-anchored match would let through.
Check 'a Kelvin sign (U+212A) for K is refused' (-not (Accepts "$([char]0x212A)ontoso.com"))
Check 'a newline before the dot is refused' (-not (Accepts "contoso`
.com"))

$message = try { & $check -Value ','; '' } catch { $_.Exception.Message }
Check 'the refusal says what to set' ($message -match 'names no domain' -and $message -match 'contoso\.com')

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
