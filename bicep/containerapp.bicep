// Container App Infrastructure
// Deploys Container App Environment, Container App, Storage Account, and Managed Identity
// Follows security best practices with user-assigned managed identity

@description('Name prefix for all resources')
param namePrefix string

@description('Azure region for resource deployment')
param location string = resourceGroup().location

@description('Environment (dev, test, prod)')
param environment string

@description('Deploy name for the application')
param deployName string

@description('Storage account SKU')
param storageAccountSku string = 'Standard_LRS'

@description('Container image to deploy')
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Schedule interval in minutes for the container app job')
param scheduleIntervalMinutes int = 30

// Variables for resource naming
var resourcePrefix = '${namePrefix}-${environment}'
var storageAccountName = replace('${resourcePrefix}${deployName}sa', '-', '')
var containerAppEnvironmentName = '${resourcePrefix}-${deployName}-env'
var containerAppName = '${resourcePrefix}-${deployName}-app'
var managedIdentityName = '${resourcePrefix}-${deployName}-mi'
var logAnalyticsWorkspaceName = '${resourcePrefix}-${deployName}-logs'

// Generate cron expression based on schedule interval (5-field format for Azure Container Apps)
var cronExpression = scheduleIntervalMinutes <= 5 ? '*/5 * * * *'     // Every 5 minutes (minimum)
                   : scheduleIntervalMinutes <= 10 ? '*/10 * * * *'    // Every 10 minutes
                   : scheduleIntervalMinutes <= 15 ? '*/15 * * * *'    // Every 15 minutes
                   : scheduleIntervalMinutes <= 30 ? '*/30 * * * *'    // Every 30 minutes
                   : scheduleIntervalMinutes <= 60 ? '0 * * * *'       // Every hour
                   : '*/30 * * * *'                                    // Default to 30 minutes

// Log Analytics Workspace for Container App Environment
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      searchVersion: 1
      legacy: 0
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

// User Assigned Managed Identity
resource userAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: managedIdentityName
  location: location
}

// Storage Account with Static Website Hosting
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: storageAccountSku
  }
  kind: 'StorageV2'
  tags: {
    SecurityControl: 'Ignore' // Allow public static website hosting
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: true // Enable for static website hosting
    allowSharedKeyAccess: true // Temporarily allow for static website configuration
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        blob: {
          enabled: true
        }
        file: {
          enabled: true
        }
      }
      keySource: 'Microsoft.Storage'
    }
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// Blob Service for Storage Account
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  name: 'default'
  parent: storageAccount
}

// Blob Container for files (kept for backward compatibility)
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: 'files'
  parent: blobService
  properties: {
    publicAccess: 'None'
  }
}

// Note: Static website hosting will be enabled manually or through the application
// Deployment script is commented out due to authentication issues with storage account key access

// Azure Container Registry
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: replace('${resourcePrefix}${deployName}acr', '-', '')
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

// Role Assignment: Storage Blob Data Contributor for Managed Identity
resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, userAssignedIdentity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') // Storage Blob Data Contributor
    principalId: userAssignedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Storage Account Contributor for managing static website hosting
resource storageAccountContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, userAssignedIdentity.id, '17d1049b-9a84-46fb-8f53-869881c3d3ab')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab') // Storage Account Contributor
    principalId: userAssignedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: ACR Pull for Managed Identity
resource acrRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, userAssignedIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') // AcrPull
    principalId: userAssignedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Module to assign Reader role at subscription scope for resource provider queries
module subscriptionReaderRole 'subscription-reader-role.bicep' = {
  name: '${resourcePrefix}-${deployName}-subscription-reader-role'
  scope: subscription()
  params: {
    principalId: userAssignedIdentity.properties.principalId
    managedIdentityName: managedIdentityName
  }
}



// Container App Environment
resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
  }
}

// Container App Job (Scheduled)
resource containerAppJob 'Microsoft.App/jobs@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${userAssignedIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppEnvironment.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 600 // 10 minutes timeout
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: userAssignedIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'filewriter'
          image: containerImage
          env: [
            {
              name: 'STORAGE_ACCOUNT_NAME'
              value: storageAccount.name
            }
            {
              name: 'CONTAINER_NAME'
              value: '$web'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: userAssignedIdentity.properties.clientId
            }
            {
              name: 'TARGET_REGION'
              value: 'belgiumcentral'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    storageRoleAssignment
    storageAccountContributorRoleAssignment
    acrRoleAssignment
    subscriptionReaderRole
  ]
}

// Outputs
@description('Storage Account name')
output storageAccountName string = storageAccount.name

@description('Container App Job name') 
output containerAppName string = containerAppJob.name

@description('Container App Environment name')
output containerAppEnvironmentName string = containerAppEnvironment.name

@description('User Assigned Managed Identity name')
output managedIdentityName string = userAssignedIdentity.name

@description('Storage Account blob endpoint')
output storageAccountBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob
