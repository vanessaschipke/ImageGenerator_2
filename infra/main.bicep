targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
@description('Name of the the environment which is used to generate a short unique hash used in all resources.')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

@description('Name of the existing storage account that the app will read/write blobs from.')
param existingStorageAccountName string = 'storage3csd2rus'

@description('Azure OpenAI endpoint used for image generation')
param azureImageEndpoint string = 'https://openai3csd2-rus.openai.azure.com'

@description('Azure OpenAI deployment name (image model)')
param azureImageDeployment string = 'gpt-image-2.5-flare'

@description('Azure OpenAI chat deployment that reads the slide image (gpt-5, version 2025-08-07).')
param azureChatDeployment string = 'gpt-5'

@description('Azure OpenAI API version')
param azureImageApiVersion string = '2025-04-01-preview'

@secure()
@description('Azure OpenAI API key. Pass via azd env (azd env set AZURE_IMAGE_API_KEY <value>).')
param azureImageApiKey string

@description('Tags applied to all resources.')
param tags object = {
  'azd-env-name': environmentName
  app: 'slidegenerator'
}

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))

// R&S networking policy requires every resource name to end with '-rus'.
var nameSuffix = '-rus'

// =====================================================================================
// App Service Plan (Linux, B1)
// =====================================================================================
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${abbrs.webServerFarms}${resourceToken}${nameSuffix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true // required for Linux
  }
}

// =====================================================================================
// App Service (Web App, .NET 8 on Linux)
// =====================================================================================
resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${abbrs.webSitesAppService}${resourceToken}${nameSuffix}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true            // wichtig fuer Blazor Server / SignalR
      webSocketsEnabled: true   // Blazor Server braucht WebSockets
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'AzureStorageBlob__Endpoint'
          value: storageAccount.properties.primaryEndpoints.blob
        }
        {
          name: 'AzureStorageBlob__ContainerName'
          value: '1slidegenerator'
        }
        {
          name: 'AzureImage__Endpoint'
          value: azureImageEndpoint
        }
        {
          name: 'AzureImage__DeploymentName'
          value: azureImageDeployment
        }
        {
          name: 'AzureImage__ChatDeploymentName'
          value: azureChatDeployment
        }
        {
          name: 'AzureImage__ApiVersion'
          value: azureImageApiVersion
        }
        {
          name: 'AzureImage__ApiKey'
          value: azureImageApiKey
        }
        {
          name: 'AzureImage__DefaultSize'
          value: '1536x1024'
        }
        {
          name: 'AzureImage__DefaultQuality'
          value: 'high'
        }
      ]
    }
  }
}

// =====================================================================================
// RBAC: Storage Blob Data Contributor for the WebApp's Managed Identity
// on the existing Storage Account
// =====================================================================================
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: existingStorageAccountName
}

// Built-in role: Storage Blob Data Contributor
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, webApp.id, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// =====================================================================================
// Outputs
// =====================================================================================
output WEB_URI string = 'https://${webApp.properties.defaultHostName}'
output WEB_APP_NAME string = webApp.name
output APP_SERVICE_PLAN_NAME string = appServicePlan.name
output WEB_APP_PRINCIPAL_ID string = webApp.identity.principalId
