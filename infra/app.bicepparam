// Parameters for app.bicep. The pipeline supplies the image and core outputs on the command line; the values
// below are the ones that describe the environment and rarely change.
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

param minReplicas = 0
param maxReplicas = 2
