#!/bin/bash
PARAMETERFILE="parameters.json"
SECRETFILE="secrets.json"

# Ensure jq is installed
if ! command -v jq &> /dev/null; then
    echo "jq is required but not installed. Install it with: sudo apt-get install jq"
    exit 1
fi

# Export variables in the current shell context
eval "$(jq -r '.parameters | to_entries[] | "export \(.key)=\(.value.value)"' $PARAMETERFILE)"
eval "$(jq -r '.parameters | to_entries[] | "export \(.key)=\(.value.value)"' $SECRETFILE)"
echo "ℹ️  Variables loaded for - ${deployName} - (if there is nothing between the dashes, the vars are not loaded)"

# Example usage:
# echo "tenantID: ${tenantID}"
# echo "subscriptionID: ${subscriptionID}"
# echo "location01: ${location01}"