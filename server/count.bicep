// The opt-in user count's service: a storage account (the pings table, and the function app's own storage) and a
// Flex Consumption function app running server/Count. Deployed by azure-pipelines-count.yml into an existing resource
// group; its names come from pipeline variables, not this file.
//
// Connection strings rather than managed identity, so the pipeline's service connection only needs Contributor on
// the resource group (granting a role would need Owner).

@description('Name of the function app. Its default address is printed as hostName; the app pings a custom domain mapped to it.')
param appName string

@description('Name of the new storage account for the count (3-24 lower-case letters and digits).')
@minLength(3)
@maxLength(24)
param storageName string

param location string = resourceGroup().location

@description('The most instances the app may scale to: a ceiling on cost if the address is flooded.')
param maximumInstanceCount int = 40

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Where the pipeline puts the app's package; Flex Consumption runs it from here.
resource deployments 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'deployments'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource pings 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tables
  name: 'pings'
}

var connection = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: connection }
        { name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING', value: connection }
        { name: 'COUNT_STORAGE', value: connection }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deployments.name}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: 512
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
  }
}

output hostName string = app.properties.defaultHostName
