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

Update the storage account, so that it has the static website enabeled.  The website should be public, without authentication.  Add the tag SecurityControl=Ignore to the storage account.

Update the container app, it should not create a hello world file, it should create a json file that contains all resource providers in the subscription for a specific region, the region should be a variable use belgiumcentral as example.  The file should contain the availability of the recource provider, the availability of the sub types of the resourec provider, for each availability zone.  Store the json file in the static website part, instead of the previous location  
Take a look at this project https://github.com/gevaertw/regionchecker/tree/dev to understand how this is done, but continue to use C#.

Update the managed identity, make sure to add the right permissions to read the resource providers from the subscription

## October 8, 2025
**Prompt:** From now on, assume bash commands, az deploy and bicep when deploying things on azure.  Put all deployment commands in the deploy.sh file, all non sensitive parameters in the parameters.json file and the secrets in the secrets.json file.  Do not change the parameters that are already defined, reuse them instead.  Store all bicep files in the bicep subfolder, and the dotnet project in the dotnet folder.  On azure, be secure and use user assigned managed identities whenever possible.  Keep everything in the same resource group on azure, if possible.  For application code, use c#, keep the code easy to read and add comments to improve readability.  For storage accounts, do not use SAS tokens or storage keys, only use managed identities.  Add each prompt to the prompts.md file.

**Solution:** Fixed deployment issues with static website configuration and updated the application to query Azure resource providers and availability zones. Changes made:
1. **Fixed Bicep template**: Removed incorrect static website configuration from blobServices and replaced with deployment script using Azure CLI
2. **Added subscription-scoped permissions**: Created separate Bicep module for Reader role assignment at subscription level
3. **Updated C# application**: Completely rewrote to query Azure Resource Manager API for resource providers, availability zones, and resource types in the target region (belgiumcentral)
4. **Enhanced container configuration**: Updated environment variables to include TARGET_REGION and changed container to $web for static website hosting
5. **Added comprehensive JSON output**: Application now generates detailed JSON file with resource provider availability data and creates HTML index page for static website



