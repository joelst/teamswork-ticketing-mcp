// Parameters for core.bicep. Values come from environment variables so the same file serves the pipeline and
// manual deployments. Never put the API key in this file: set it with infra/scripts/Set-TicketingApiKey.ps1
// or pass it as a secure pipeline variable via --parameters ticketingApiKey=...
using './core.bicep'

param namePrefix = readEnvironmentVariable('TAAS_NAME_PREFIX', 'taasmcp')
param environmentName = readEnvironmentVariable('TAAS_ENVIRONMENT', 'prod')
param logRetentionDays = 30
param logDailyCapGb = 1
