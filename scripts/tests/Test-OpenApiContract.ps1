$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot '..\Test-TicketingOpenApi.ps1'
$tempFile = New-TemporaryFile

try {
    $operations = @(
        [pscustomobject]@{ Method = 'GET'; Path = '/tickets' },
        [pscustomobject]@{ Method = 'POST'; Path = '/tickets' },
        [pscustomobject]@{ Method = 'GET'; Path = '/tickets/{ticketId}' },
        [pscustomobject]@{ Method = 'PUT'; Path = '/tickets/{ticketId}' },
        [pscustomobject]@{ Method = 'PUT'; Path = '/tickets/{ticketId}/status' },
        [pscustomobject]@{ Method = 'GET'; Path = '/tickets/{ticketId}/activities' },
        [pscustomobject]@{ Method = 'POST'; Path = '/tickets/{ticketId}/activities' },
        [pscustomobject]@{ Method = 'GET'; Path = '/tickets/{ticketId}/attachments' },
        [pscustomobject]@{ Method = 'POST'; Path = '/tickets/{ticketId}/attachments' },
        [pscustomobject]@{ Method = 'GET'; Path = '/tickets/activity/{activityId}/attachments' },
        [pscustomobject]@{ Method = 'GET'; Path = '/instance' },
        [pscustomobject]@{ Method = 'GET'; Path = '/tags' }
    )
    $paths = [ordered]@{}
    foreach ($operation in $operations) {
        if (-not $paths.Contains($operation.Path)) { $paths[$operation.Path] = [ordered]@{} }
        $paths[$operation.Path][$operation.Method.ToLowerInvariant()] = @{ responses = @{ '200' = @{ description = 'OK' } } }
    }

    @{ openapi = '3.0.0'; paths = $paths } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tempFile.FullName -Encoding UTF8
    & $checker -OpenApiPath $tempFile.FullName

    [void]$paths.Remove('/tags')
    @{ openapi = '3.0.0'; paths = $paths } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tempFile.FullName -Encoding UTF8
    $missingDetected = $false
    try { & $checker -OpenApiPath $tempFile.FullName }
    catch { $missingDetected = $_.Exception.Message.IndexOf('GET /tags', [StringComparison]::Ordinal) -ge 0 }
    if (-not $missingDetected) { throw 'The contract checker did not report the removed GET /tags operation.' }
}
finally {
    Remove-Item -LiteralPath $tempFile.FullName -Force
}
