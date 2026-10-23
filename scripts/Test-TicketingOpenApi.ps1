[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OpenApiPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $OpenApiPath -PathType Leaf)) {
    throw "OpenAPI file not found: $OpenApiPath. Download the latest JSON from the official TeamsWork API reference."
}

$document = Get-Content -LiteralPath $OpenApiPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $document.openapi -and -not $document.swagger) {
    throw 'The supplied JSON is not an OpenAPI or Swagger document.'
}
if (-not $document.paths) {
    throw 'The OpenAPI document has no paths.'
}

$actual = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($pathProperty in $document.paths.PSObject.Properties) {
    $normalizedPath = [regex]::Replace($pathProperty.Name.TrimEnd('/'), '\{[^/]+\}', '{}')
    foreach ($operation in $pathProperty.Value.PSObject.Properties) {
        $method = $operation.Name.ToUpperInvariant()
        if ($method -in @('GET', 'POST', 'PUT', 'PATCH', 'DELETE')) {
            [void]$actual.Add("$method $normalizedPath")
        }
    }
}

$required = @(
    'GET /tickets',
    'POST /tickets',
    'GET /tickets/{}',
    'PUT /tickets/{}',
    'PUT /tickets/{}/status',
    'GET /tickets/{}/activities',
    'POST /tickets/{}/activities',
    'GET /tickets/{}/attachments',
    'POST /tickets/{}/attachments',
    'GET /tickets/activity/{}/attachments',
    'GET /instance',
    'GET /tags'
)
$missing = @($required | Where-Object { -not $actual.Contains($_) })
if ($missing) {
    throw "The vendor OpenAPI document is missing operations used by this server: $($missing -join ', '). Review the API contract before upgrading."
}

Write-Output "OpenAPI contract includes all $($required.Count) required Ticketing API operations."
