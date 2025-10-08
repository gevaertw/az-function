#!/bin/bash
source loadvar.sh

# Ensure Azure CLI is installed

#if ! command -v az &> /dev/null; then
#    echo "Azure CLI is required but not installed. Installing Azure CLI..."
    # curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
    # specific version for my devbox, later versions have a bug
#    sudo apt-get install -y --allow-downgrades azure-cli=2.72.0-1~noble
#fi



# Log in to Azure if not already logged in
az account show &> /dev/null
if [ $? -ne 0 ]; then
    # echo "Logging in to Azure..."
    az login --tenant "${tenantID}" --use-device-code
fi

# Set the subscription context
az account set --subscription "${subscriptionID}"
echo "ℹ️  Using subscription: ${subscriptionID} in tenant: ${tenantID}"

# Minimal: set SPN variable to current principal (user UPN or service principal name)
myUserSPN=$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null || az account get-access-token --query clientId -o tsv 2>/dev/null | xargs -I{} az ad sp show --id {} --query "servicePrincipalNames[0]" -o tsv 2>/dev/null || true)
echo "ℹ️  User: ${myUserSPN}"
myUserSID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || az account get-access-token --query clientId -o tsv 2>/dev/null | xargs -I{} az ad sp show --id {} --query id -o tsv 2>/dev/null || true)
echo "ℹ️  SID: ${myUserSID}"