// Subscription-scoped role assignment for managed identity
// Grants Reader permission to query resource providers

targetScope = 'subscription'

@description('Principal ID of the managed identity')
param principalId string

@description('Managed identity name for unique role assignment name')
param managedIdentityName string

// Role Assignment: Reader for Resource Provider queries at subscription scope
resource subscriptionReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, principalId, managedIdentityName, 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7') // Reader
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// Output the role assignment ID for reference
output roleAssignmentId string = subscriptionReaderRoleAssignment.id
