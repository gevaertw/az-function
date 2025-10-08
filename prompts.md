# Context prompt
From now on, assume bash commands, az deploy and bicep when deploying things on azure.  Put all deployment commands in the deploy.sh file, all non sensitive parameters in the parameters.json file and the secrets in the secrets.json file.  Do not change the parameters that are already defined, reuse them instead.  Store all bicep files in the bicep subfolder, and the dotnet project in the dotnet folder.  On azure, be secure and use user assigned managed identities whenever possible.  Keep everything in the same resource group on azure, if possible.  For application code, use c#, keep the code easy to read and add comments to improve readability.  For storage accounts, do not use SAS tokens or storage keys, only use managed identities.  Add each prompt to the prompts.md file.    

# Prompts History

## October 7, 2025
**Prompt:** From now on, assume bash commands, az deploy and bicep when deploying things on azure.  Put all deployment commands in the deploy.sh file, all non sensitive parameters in the parameters.json file and the secrets in the secrets.json file.  Do not change the parameters that are already defined, reuse them instead.  Store all bicep files in the bicep subfolder, and the dotnet project in the dotnet folder.  On azure, be secure and use user assigned managed identities whenever possible.  Add each prompt to the prompts.md file.

**Prompt:** For application code, use c#, keep the code easy to read and add comments to improve readability.

**Prompt:** Keep everything in the same resource group on azure, if possible.

**Prompt:** For storage accounts, do not use SAS tokens or storage keys, only use managed identities.

**Request:** Create a container app that writes a file helloworld.txt to a storage account, the file contains the text "1234"

**Issue:** Docker is not installed on RHEL VM - need to use ACR Build for containerization

**Issue:** Template parameter mismatch - Bicep template parameters don't match parameters.json file

**Solution:** Updated deployment script to use ACR Build instead of Docker, fixed Bicep template with proper ACR integration and role assignments

**Success:** Container App deployed successfully and wrote helloworld.txt file containing "1234" to storage account using managed identity authentication
