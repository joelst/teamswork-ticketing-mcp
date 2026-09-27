// Parameters for core.bicep, for manual deployments (az deployment group create --parameters infra/core.bicepparam).
// The pipeline doesn't read this file: it passes its values on the command line, and the template's defaults cover
// the rest, so a change here doesn't reach a pipeline deployment. Both pipelines compile it against the template.
// Never put the API key in this file: set it with infra/scripts/Set-TicketingApiKey.ps1 or pass it as a secure
// pipeline variable via --parameters ticketingApiKey=...
using './core.bicep'

param namePrefix = readEnvironmentVariable('TAAS_NAME_PREFIX', 'taasmcp')
param environmentName = readEnvironmentVariable('TAAS_ENVIRONMENT', 'prod')
param logRetentionDays = 30
param logDailyCapGb = 1
