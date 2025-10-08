#!/bin/bash
#loading vars and login to Azure
source loadvar.sh
source az-auth.sh

# Deploy Container App infrastructure
echo "ℹ️  Creating Resource group ${rgname} in location ${location}"
az group create \
    --name "${rgname}" \
    --location "${location}"

# Deploy infrastructure first to create ACR
echo "🏗️  Deploying infrastructure (without container app)..."
az deployment group create \
    --resource-group "${rgname}" \
    --template-file "bicep/containerapp.bicep" \
    --parameters namePrefix="${namePrefix}" \
    --parameters location="${location}" \
    --parameters environment="${environment}" \
    --parameters deployName="${deployName}" \
    --parameters storageAccountSku="${storageAccountSku:-Standard_LRS}" \
    --parameters containerImage="mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"

# Get ACR name and build container image using ACR Build
acrName="${namePrefix}${environment}${deployName}acr"
echo "🐳 Building container image using ACR Build..."
cd dotnet/FileWriter
az acr build \
    --registry "${acrName}" \
    --image "filewriter:latest" \
    --file "Dockerfile" \
    .

# Update parameters with correct ACR image URL
containerImage="${acrName}.azurecr.io/filewriter:latest"ogin to Azure
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

# Build container image using ACR Build (no local Docker required)
echo "� Building container image using ACR Build..."
cd dotnet/FileWriter
az acr build \
    --registry "${acrName}" \
    --image "filewriter:latest" \
    --file "Dockerfile" \
    .

# Update parameters with correct ACR image URL
containerImage="${acrName}.azurecr.io/filewriter:latest"

# Deploy infrastructure again with the correct container image
echo "🏗️  Updating Container App with built image..."
cd ../../
az deployment group create \
    --resource-group "${rgname}" \
    --template-file "bicep/containerapp.bicep" \
    --parameters namePrefix="${namePrefix}" \
    --parameters location="${location}" \
    --parameters environment="${environment}" \
    --parameters deployName="${deployName}" \
    --parameters storageAccountSku="${storageAccountSku:-Standard_LRS}" \
    --parameters containerImage="${containerImage}"

echo "✅ Deployment completed successfully!"

