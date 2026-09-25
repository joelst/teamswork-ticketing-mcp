// ---------------------------------------------------------------------------------------------------------------
// The MCP server container app. Deployed after core.bicep and after the image has been pushed to ACR.
// Scale-to-zero Consumption workload: costs nothing while idle, a few cents under normal agent traffic.
// ---------------------------------------------------------------------------------------------------------------
targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string = 'taasmcp'

@minLength(2)
@maxLength(8)
param environmentName string = 'prod'

param location string = resourceGroup().location

@description('Full image reference, e.g. myacr.azurecr.io/teamswork-taas-mcp:1234.')
param containerImage string

@description('Name of the Container Apps environment created by core.bicep.')
param containerAppsEnvironmentName string

@description('Resource ID of the user-assigned identity created by core.bicep.')
param identityId string

@description('ACR login server, e.g. myacr.azurecr.io.')
param registryLoginServer string

@description('Key Vault secret URI holding the Ticketing API key (versionless), from core.bicep output apiKeySecretUri.')
param apiKeySecretUri string

@description('Microsoft Entra tenant ID that issues tokens for the server.')
param entraTenantId string

@description('Application (client) ID of the MCP server app registration.')
param entraClientId string

@description('Optional public base URL (custom domain) used in protected-resource metadata. Leave empty to derive from the request.')
param publicBaseUrl string = ''

@description('Upstream Ticketing API base URL.')
param ticketingBaseUrl string = 'https://teamswork.azure-api.net/ticketing/v1'

@description('IANA time zone used to compute the default timezone offset.')
param defaultTimeZoneId string = 'America/Chicago'

@description('Entra object ID of the service account used to attribute writes made with app-only tokens (e.g. Foundry managed identity). Optional.')
param serviceAccountId string = ''
param serviceAccountName string = ''
param serviceAccountEmail string = ''

@minValue(0)
@maxValue(10)
param minReplicas int = 0

@minValue(1)
@maxValue(10)
param maxReplicas int = 2

param tags object = {
  workload: 'teamswork-taas-mcp'
  environment: environmentName
}

var appName = '${namePrefix}-${environmentName}-app'

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: containerAppsEnvironmentName
}

var serviceAccountEnv = empty(serviceAccountId) ? [] : [
  { name: 'Ticketing__ServiceAccount__Id', value: serviceAccountId }
  { name: 'Ticketing__ServiceAccount__Name', value: serviceAccountName }
  { name: 'Ticketing__ServiceAccount__Email', value: serviceAccountEmail }
]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 3
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: registryLoginServer
          identity: identityId
        }
      ]
      secrets: [
        {
          name: 'ticketing-api-key'
          keyVaultUrl: apiKeySecretUri
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mcp'
          image: containerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat([
            { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DOTNET_EnableDiagnostics', value: '0' }
            { name: 'Ticketing__ApiKey', secretRef: 'ticketing-api-key' }
            { name: 'Ticketing__BaseUrl', value: ticketingBaseUrl }
            { name: 'Ticketing__DefaultTimeZoneId', value: defaultTimeZoneId }
            { name: 'Entra__TenantId', value: entraTenantId }
            { name: 'Entra__ClientId', value: entraClientId }
            { name: 'Entra__PublicBaseUrl', value: publicBaseUrl }
          ], serviceAccountEnv)
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/healthz', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 2
              periodSeconds: 3
              failureThreshold: 10
            }
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080, scheme: 'HTTP' }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/healthz', port: 8080, scheme: 'HTTP' }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '20'
              }
            }
          }
        ]
      }
    }
  }
}

output containerAppName string = app.name
output containerAppFqdn string = app.properties.configuration.ingress.fqdn
output mcpEndpoint string = 'https://${app.properties.configuration.ingress.fqdn}/mcp'
output latestRevisionName string = app.properties.latestRevisionName
