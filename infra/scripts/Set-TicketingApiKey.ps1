<#
.SYNOPSIS
    Stores the TeamsWork Ticketing API key in Azure Key Vault without echoing it.

.DESCRIPTION
    Prompts for the key as a secure string (or reads it from the TICKETING_API_KEY environment variable when
    -FromEnvironment is used, e.g. in a pipeline), then writes it to the Key Vault secret the container app reads.
    The caller needs the "Key Vault Secrets Officer" role on the vault.

.EXAMPLE
    ./Set-TicketingApiKey.ps1 -KeyVaultName taasmcp-prod-kv-abc123
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $KeyVaultName,
    [string] $SecretName = 'Ticketing--ApiKey',
    [switch] $FromEnvironment
)

$ErrorActionPreference = 'Stop'

if ($FromEnvironment) {
    $plain = $env:TICKETING_API_KEY
    if ([string]::IsNullOrWhiteSpace($plain)) { throw 'TICKETING_API_KEY is not set.' }
}
else {
    $secure = Read-Host -Prompt 'Paste the Ticketing instance API key' -AsSecureString
    $plain = [System.Net.NetworkCredential]::new('', $secure).Password
    if ([string]::IsNullOrWhiteSpace($plain)) { throw 'No key entered.' }
}

# Pass the value via a temp file rather than the command line so it does not land in shell history / process lists.
$tmp = New-TemporaryFile
try {
    Set-Content -Path $tmp -Value $plain -NoNewline -Encoding utf8
    $result = az keyvault secret set --vault-name $KeyVaultName --name $SecretName --file $tmp --content-type 'text/plain' --query 'id' -o tsv 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az keyvault secret set failed: $result" }
    Write-Host "Stored secret '$SecretName' in vault '$KeyVaultName'."
    Write-Host 'If the container app is already running, restart its revision so it picks up the new value:'
    Write-Host "  az containerapp revision restart -g <rg> -n <app> --revision <revision>"
}
finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    $plain = $null
}
