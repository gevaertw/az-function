using Azure.Identity;
using Azure.Storage.Blobs;
using System.Text;

// FileWriter - Azure Container App that writes helloworld.txt to Storage Account
// Uses managed identity for secure authentication to Azure Storage

// Get the storage account name from environment variable
var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
if (string.IsNullOrEmpty(storageAccountName))
{
    Console.WriteLine("❌ ERROR: STORAGE_ACCOUNT_NAME environment variable is required");
    Environment.Exit(1);
}

// Get the container name from environment variable (default to 'files')
var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME") ?? "files";

try
{
    Console.WriteLine("🚀 Starting FileWriter application...");
    Console.WriteLine($"📁 Target Storage Account: {storageAccountName}");
    Console.WriteLine($"📦 Target Container: {containerName}");

    // Create BlobServiceClient using DefaultAzureCredential (managed identity)
    // This will use the user-assigned managed identity configured for the container app
    var blobServiceUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
    var credential = new DefaultAzureCredential();
    var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

    Console.WriteLine("🔐 Successfully authenticated using managed identity");

    // Get reference to the blob container
    var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    
    // Ensure the container exists (create if it doesn't)
    Console.WriteLine($"📦 Ensuring container '{containerName}' exists...");
    await containerClient.CreateIfNotExistsAsync();

    // Create the file content
    var fileName = "helloworld.txt";
    var fileContent = "1234";
    var contentBytes = Encoding.UTF8.GetBytes(fileContent);

    Console.WriteLine($"📝 Writing file '{fileName}' with content: '{fileContent}'");

    // Get reference to the blob and upload the content
    var blobClient = containerClient.GetBlobClient(fileName);
    
    using (var stream = new MemoryStream(contentBytes))
    {
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    Console.WriteLine($"✅ Successfully uploaded '{fileName}' to storage account '{storageAccountName}'");
    Console.WriteLine($"📍 Blob URL: {blobClient.Uri}");
    
    // Verify the upload by checking if the blob exists
    var exists = await blobClient.ExistsAsync();
    if (exists.Value)
    {
        Console.WriteLine("✅ File upload verified successfully");
    }
    else
    {
        Console.WriteLine("⚠️  Warning: Could not verify file upload");
    }

    Console.WriteLine("🎉 FileWriter application completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ ERROR: {ex.Message}");
    Console.WriteLine($"🔍 Details: {ex}");
    Environment.Exit(1);
}
