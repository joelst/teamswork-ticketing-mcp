# Operations and telemetry

## Metrics

HTTP deployments create a workspace-based Application Insights resource in the Log Analytics workspace. The server
exports metrics when `ApplicationInsights:ConnectionString` is set; the pipeline supplies the connection string
created by `infra/core.bicep`, as does `infra/scripts/Deploy-TestInstance.ps1`. No ticket content, caller identity,
ticket ID, API key, or raw URL is used as a metric dimension. Only HTTP method, route category, upstream operation,
and status are tagged, keeping cardinality bounded.
Unrecognized incoming HTTP methods are recorded as `_OTHER`; transport failures use a bounded `error.type` value
instead of masquerading as an HTTP status code.

The custom metrics are:

| Metric | What it measures |
| --- | --- |
| `ticketing.mcp.http.requests` | MCP, health, and other HTTP requests |
| `ticketing.mcp.http.duration` | HTTP request duration in milliseconds |
| `ticketing.upstream.requests` | Individual Ticketing API attempts, including retries |
| `ticketing.upstream.duration` | Time to upstream response headers, in milliseconds |
| `ticketing.upstream.retries` | Retries initiated after a connection failure or retryable response |
| `ticketing.upstream.response.size` | Read upstream response bytes |

Example query in Application Insights Logs:

```kusto
customMetrics
| where timestamp > ago(24h)
| where name == "ticketing.upstream.requests"
| summarize requests = sum(value)
    by bin(timestamp, 5m),
       operation = tostring(customDimensions["ticketing.operation"]),
       status = tostring(customDimensions["http.response.status_code"]),
       error = tostring(customDimensions["error.type"])
| order by timestamp desc
```

Container Apps metrics, platform logs, and Application Insights ingestion have separate billing and retention
behavior. The app's custom metric volume is bounded by its fixed route/operation/status dimensions; review workspace
retention and ingestion limits when changing traffic or the number of instruments.

## Logs and common signals

Container Apps sends application console output to `ContainerAppConsoleLogs_CL`. This query counts common signals
without returning raw log messages or ticket content:

```kusto
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(24h)
| where ContainerAppName_s == "taasmcp-prod-app"
| where Log_s has_any ("warn", "fail", "error") or Log_s contains "Exception"
| extend signal = case(
    Log_s contains "used your share of the Ticketing API quota", "caller quota",
    Log_s contains "returned 429", "upstream throttled",
    Log_s contains "Exception", "exception",
    Log_s has "warn", "warning",
    "error")
| summarize occurrences = count() by signal
| order by occurrences desc
```

Interpret common signals as follows:

- `used your share of the Ticketing API quota`: the per-caller cap is working. Reduce scan sizes or concurrent agent
  activity; don't raise the cap above the vendor's process-wide 100 requests per minute.
- `returned 429` or `returned 5xx`: upstream throttling or service trouble. Retries are bounded; inspect status,
  operation, and duration metrics.
- `outcome is unknown` on a write: the upstream may have processed the request. Check the ticket before retrying.
- Repeated `connection failed`, `timeout`, or empty/invalid JSON errors: check the selected region, endpoint health,
  network path, and vendor status.
- A high count of `McpException` for invalid arguments or unknown tools can indicate stale tool descriptions or
  client prompts; compare against the current tool list before adding compatibility behavior.

The service is stateless and uses no cookie or server-side session persistence. Its upstream quotas, caller shares,
and cache live in process memory; `infra/app.bicep` therefore caps the deployment at one replica. Scaling beyond one
requires moving those shared limits to a distributed store first.

Logs and metrics use separate pipelines: console logs continue to go to Log Analytics, while Application Insights
receives custom metrics only. The Application Insights connection string is configuration, not an API credential;
the Ticketing API key remains in Key Vault.

## macOS release signing

The release workflow supports optional Developer ID signing and notarization for the macOS executable. Enable the
repository variable `MACOS_SIGNING_ENABLED=true` and configure these secrets in the GitHub `release` environment:
`APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_ID`, `APPLE_TEAM_ID`, and
`APPLE_APP_SPECIFIC_PASSWORD`. Use a Developer ID Application certificate exported as base64-encoded PKCS#12.
When enabled, a missing value or an unsuitable certificate fails the macOS release job rather than silently
publishing an unsigned build. When disabled, the release remains unsigned.
