// Parameters for app.bicep, for manual deployments (az deployment group create --parameters infra/app.bicepparam).
// The pipeline doesn't read this file: it passes the image, the core outputs and its variable group's values on the
// command line, and the template's defaults cover the rest (replicas, time zone), so a change here doesn't reach a
// pipeline deployment. Both pipelines compile it against the template.
using './app.bicep'

param namePrefix = readEnvironmentVariable('TAAS_NAME_PREFIX', 'taasmcp')
param environmentName = readEnvironmentVariable('TAAS_ENVIRONMENT', 'prod')

param containerImage = readEnvironmentVariable('TAAS_CONTAINER_IMAGE', '')
param containerAppsEnvironmentName = readEnvironmentVariable('TAAS_CAE_NAME', '')
param identityId = readEnvironmentVariable('TAAS_IDENTITY_ID', '')
param registryLoginServer = readEnvironmentVariable('TAAS_ACR_LOGIN_SERVER', '')
param apiKeySecretUri = readEnvironmentVariable('TAAS_API_KEY_SECRET_URI', '')

param entraTenantId = readEnvironmentVariable('TAAS_ENTRA_TENANT_ID', '')
param entraClientId = readEnvironmentVariable('TAAS_ENTRA_CLIENT_ID', '')
param publicBaseUrl = readEnvironmentVariable('TAAS_PUBLIC_BASE_URL', '')

param defaultTimeZoneId = 'America/Chicago'
param serviceAccountId = readEnvironmentVariable('TAAS_SERVICE_ACCOUNT_ID', '')
param serviceAccountName = readEnvironmentVariable('TAAS_SERVICE_ACCOUNT_NAME', '')
param serviceAccountEmail = readEnvironmentVariable('TAAS_SERVICE_ACCOUNT_EMAIL', '')
param externalEmailDomains = readEnvironmentVariable('TAAS_EXTERNAL_EMAIL_DOMAINS', '')

param minReplicas = 0
param maxReplicas = 1 // app.bicep allows only one; see the note there
