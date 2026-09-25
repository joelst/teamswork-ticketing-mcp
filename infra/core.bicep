// ---------------------------------------------------------------------------------------------------------------
// Core infrastructure for the TeamsWork Ticketing MCP server.
// Deployed first (and idempotently on every pipeline run). Creates everything the container app depends on:
//   Log Analytics, user-assigned identity, ACR (Basic), Key Vault (RBAC), Container Apps environment, role assignments.
// The container app itself lives in app.bicep so the image can be pushed to ACR in between.
// ---------------------------------------------------------------------------------------------------------------
targetScope = 'resourceGroup'

@description('Short name prefix for all resources (3-12 lowercase letters/digits).')
@minLength(3)
@maxLength(12)
param namePrefix string = 'taasmcp'

@description('Environment label used in resource names, e.g. prod.')
@minLength(2)
@maxLength(8)
param environmentName string = 'prod'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Days to keep logs. 30 is the free tier for Log Analytics.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('Daily ingestion cap in GB to keep Log Analytics cost bounded.')
param logDailyCapGb int = 1

@description('Optional: Ticketing API key to store in Key Vault. Leave empty to set it out-of-band with infra/scripts/Set-TicketingApiKey.ps1.')
@secure()
param ticketingApiKey string = ''

@description('Key Vault secret name. The double dash maps to the Ticketing:ApiKey configuration key for local runs.')
param apiKeySecretName string = 'Ticketing--ApiKey'

@description('Tags applied to every resource.')
param tags object = {
  workload: 'teamswork-taas-mcp'
  environment: environmentName
}

var suffix = toLower(uniqueString(resourceGroup().id))
var base = '${namePrefix}-${environmentName}'
var logAnalyticsName = '${base}-law'
var identityName = '${base}-id'
var containerAppsEnvName = '${base}-cae'
var registryName = toLower(replace('${namePrefix}${environmentName}acr${suffix}', '-', ''))
var keyVaultName = take('${base}-kv-${suffix}', 24)

// Built-in role definition IDs
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
    workspaceCapping: {
      dailyQuotaGb: logDailyCapGb
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
  }
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Only created when the key is supplied as a secure parameter; otherwise it is set out-of-band.
resource apiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(ticketingApiKey)) {
  parent: keyVault
  name: apiKeySecretName
  tags: tags
  properties: {
    value: ticketingApiKey
    contentType: 'text/plain'
  }
}

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppsEnvName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

output logAnalyticsWorkspaceId string = logAnalytics.id
output identityId string = identity.id
output identityPrincipalId string = identity.properties.principalId
output identityClientId string = identity.properties.clientId
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
output apiKeySecretUri string = '${keyVault.properties.vaultUri}secrets/${apiKeySecretName}'
output containerAppsEnvironmentId string = containerAppsEnv.id
output containerAppsEnvironmentName string = containerAppsEnv.name
