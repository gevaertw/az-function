#!/bin/bash
# Deploy script for Azure Container App Job with scheduled execution
# Loading variables and login to Azure
source loadvar.sh
source az-auth.sh

# Deploy Container App infrastructure
echo "ℹ️  Creating Resource group ${rgname} in location ${location}"
az group create \
    --name "${rgname}" \
    --location "${location}"

# Create Azure Container Registry if it doesn't exist
acrName="${namePrefix}${environment}${deployName}acr"
echo "🗃️  Creating Azure Container Registry ${acrName}..."
az acr create \
    --resource-group "${rgname}" \
    --name "${acrName}" \
    --sku Basic \
    --location "${location}"

# Deploy infrastructure first to create other resources
echo "🏗️  Deploying infrastructure..."
az deployment group create \
    --resource-group "${rgname}" \
    --template-file "bicep/containerapp.bicep" \
    --parameters namePrefix="${namePrefix}" \
    --parameters location="${location}" \
    --parameters environment="${environment}" \
    --parameters deployName="${deployName}" \
    --parameters storageAccountSku="${storageAccountSku:-Standard_LRS}" \
    --parameters containerImage="mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" \
    --parameters scheduleIntervalMinutes="${scheduleIntervalMinutes}"

# Build container image using ACR Build (no local Docker required)
echo "🐳 Building container image using ACR Build..."
cd dotnet/FileWriter
az acr build \
    --registry "${acrName}" \
    --image "filewriter:latest" \
    --file "Dockerfile" \
    .

# Update Container App Job with the built container image
buildContainerImage="${acrName}.azurecr.io/filewriter:latest"
echo "🏗️  Updating Container App Job with built image..."
cd ../../
az deployment group create \
    --resource-group "${rgname}" \
    --template-file "bicep/containerapp.bicep" \
    --parameters namePrefix="${namePrefix}" \
    --parameters location="${location}" \
    --parameters environment="${environment}" \
    --parameters deployName="${deployName}" \
    --parameters storageAccountSku="${storageAccountSku:-Standard_LRS}" \
    --parameters containerImage="${buildContainerImage}" \
    --parameters scheduleIntervalMinutes="${scheduleIntervalMinutes}"

echo "✅ Deployment completed successfully!"
echo "📋 Container App Job will run every ${scheduleIntervalMinutes} minutes"

