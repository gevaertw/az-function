using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Compute;
using Azure.Storage.Blobs;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using Azure.Core;

// ResourceChecker - Azure Container App that queries resource providers and availability zones
// Uses managed identity for secure authentication to Azure Storage and Resource Manager

// Get the storage account name from environment variable
var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
if (string.IsNullOrEmpty(storageAccountName))
{
    Console.WriteLine("❌ ERROR: STORAGE_ACCOUNT_NAME environment variable is required");
    Environment.Exit(1);
}

// Get the container name from environment variable (default to '$web')
var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME") ?? "$web";

// Get the target region from environment variable (default to 'belgiumcentral')
var targetRegion = Environment.GetEnvironmentVariable("TARGET_REGION") ?? "belgiumcentral";

try
{
    Console.WriteLine("🚀 Starting ResourceChecker application...");
    Console.WriteLine($"📁 Target Storage Account: {storageAccountName}");
    Console.WriteLine($"📦 Target Container: {containerName}");
    Console.WriteLine($"🌍 Target Region: {targetRegion}");

    // Create Azure Resource Manager client using DefaultAzureCredential (managed identity)
    var credential = new DefaultAzureCredential();
    var armClient = new ArmClient(credential);

    Console.WriteLine("🔐 Successfully authenticated using managed identity");

    // Get the default subscription
    var subscription = await armClient.GetDefaultSubscriptionAsync();
    Console.WriteLine($"📋 Subscription ID: {subscription.Id.SubscriptionId}");

    // Get locations with availability zone mappings for the subscription
    Console.WriteLine($"🔍 Querying locations and availability zones...");
    var locations = subscription.GetLocationsAsync();
    
    // Filter for the target region and build resource provider data
    var resourceProviderData = new
    {
        region = targetRegion,
        generatedAt = DateTime.UtcNow,
        resourceProviders = new List<object>()
    };

    await foreach (var location in locations)
    {
        // Only process the target region
        if (!location.Name.Equals(targetRegion, StringComparison.OrdinalIgnoreCase))
            continue;

        Console.WriteLine($"📍 Processing location: {location.DisplayName} ({location.Name})");
        Console.WriteLine($"🏗️  Available zones: {(location.AvailabilityZoneMappings?.Count > 0 ? string.Join(", ", location.AvailabilityZoneMappings.Select(z => z.LogicalZone)) : "None")}");

        // Get resource providers for this subscription
        var resourceProvidersCollection = subscription.GetResourceProviders();
        var resourceProviders = resourceProvidersCollection.GetAllAsync();
        
        await foreach (var provider in resourceProviders)
        {
            // Include ALL providers regardless of registration state
            // This gives the complete picture like the Azure Management API

            var resourceTypes = new List<object>();
            var hasResourcesInRegion = false;

            // Process each resource type for this provider
            if (provider.Data.ResourceTypes != null)
            {
                foreach (var resourceType in provider.Data.ResourceTypes)
                {
                    // Check if this resource type is available in the target location
                    var isAvailableInLocation = resourceType.Locations?.Any(loc => 
                        loc.Equals(location.Name, StringComparison.OrdinalIgnoreCase) ||
                        loc.Equals(location.DisplayName, StringComparison.OrdinalIgnoreCase)) ?? false;

                    var resourceTypeInfo = new
                    {
                        name = resourceType.ResourceType,
                        availableInRegion = isAvailableInLocation,
                        availabilityZones = isAvailableInLocation ? (location.AvailabilityZoneMappings?.Select(z => (object)new
                        {
                            logicalZone = z.LogicalZone,
                            physicalZone = z.PhysicalZone
                        }).ToList() ?? new List<object>()) : new List<object>(),
                        apiVersions = resourceType.ApiVersions?.Take(5).ToList() ?? new List<string>(), // Latest 5 versions
                        locations = resourceType.Locations?.ToList() ?? new List<string>()
                    };

                    resourceTypes.Add(resourceTypeInfo);
                    
                    if (isAvailableInLocation)
                    {
                        hasResourcesInRegion = true;
                    }
                }
            }

            var providerInfo = new
            {
                namespace_ = provider.Data.Namespace,
                registrationState = provider.Data.RegistrationState,
                resourceTypes = resourceTypes,
                hasResourcesInRegion = hasResourcesInRegion
            };

            // Add ALL providers (both with and without resources in the target region)
            ((List<object>)resourceProviderData.resourceProviders).Add(providerInfo);
        }

        break; // Only process the target region
    }

    Console.WriteLine($"📊 Found {((List<object>)resourceProviderData.resourceProviders).Count} resource providers with resources in {targetRegion}");

    // Collect VM SKU information
    Console.WriteLine("🖥️ Collecting VM SKU information...");
    var vmSkuData = await CollectVmSkuData(subscription, targetRegion);
    Console.WriteLine($"💻 Found {vmSkuData.Count} VM SKUs in {targetRegion}");

    // Convert to JSON
    var jsonOptions = new JsonSerializerOptions 
    { 
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    var jsonContent = JsonSerializer.Serialize(resourceProviderData, jsonOptions);
    var contentBytes = Encoding.UTF8.GetBytes(jsonContent);

    // Upload to storage account
    Console.WriteLine("📤 Uploading resource provider data to storage account...");
    
    var blobServiceUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
    var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

    // Get reference to the blob container (should be $web for static website)
    var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    
    // Create the file
    var fileName = "resource-providers.json";
    var blobClient = containerClient.GetBlobClient(fileName);
    
    using (var stream = new MemoryStream(contentBytes))
    {
        await blobClient.UploadAsync(stream, overwrite: true);
        
        // Set content type for JSON
        await blobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "application/json"
        });
    }

    Console.WriteLine($"✅ Successfully uploaded '{fileName}' to storage account '{storageAccountName}'");
    Console.WriteLine($"📍 Blob URL: {blobClient.Uri}");
    
    // Upload VM SKU JSON data
    await UploadVmSkuJson(containerClient, vmSkuData, targetRegion);
    
    // Upload CSS file
    Console.WriteLine("🎨 Uploading CSS styles...");
    await UploadCssFile(containerClient);
    
    // Create index.html file with proper styling and navigation
    Console.WriteLine("🏠 Generating index.html...");
    await CreateIndexPage(containerClient, targetRegion, resourceProviderData, fileName);
    
    // Create resources.html file with resource provider table
    Console.WriteLine("📊 Generating resources.html...");
    await CreateResourcesPage(containerClient, targetRegion, resourceProviderData);
    
    // Create vm-skus.html file with VM SKU table
    Console.WriteLine("💻 Generating vm-skus.html...");
    await CreateVmSkusPage(containerClient, targetRegion, vmSkuData);

    Console.WriteLine($"✅ Successfully uploaded all HTML files to storage account '{storageAccountName}'");
    Console.WriteLine($"🌐 Static website URL: https://{storageAccountName}.z1.web.core.windows.net/");
    
    Console.WriteLine("🎉 ResourceChecker application completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ ERROR: {ex.Message}");
    Console.WriteLine($"🔍 Details: {ex}");
    Environment.Exit(1);
}

// Helper method to upload CSS file
static async Task UploadCssFile(BlobContainerClient containerClient)
{
    var cssContent = GetCssContent();
    var cssBytes = Encoding.UTF8.GetBytes(cssContent);
    var cssBlobClient = containerClient.GetBlobClient("azure_unified_styles.css");
    
    using (var cssStream = new MemoryStream(cssBytes))
    {
        await cssBlobClient.UploadAsync(cssStream, overwrite: true);
        await cssBlobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "text/css"
        });
    }
    
    Console.WriteLine("✅ Successfully uploaded 'azure_unified_styles.css'");
}

// Helper method to get CSS content
static string GetCssContent()
{
    return @"/* 
 * Azure Resource Dashboard - Unified Styles
 * Microsoft Learn inspired design system
 */

body {
    font-family: ""Segoe UI"", -apple-system, BlinkMacSystemFont, ""Roboto"", ""Helvetica Neue"", sans-serif;
    margin: 0;
    padding: 0;
    background-color: #ffffff;
    color: #323130;
    line-height: 1.5;
}

.main-nav {
    background-color: #ffffff;
    border-bottom: 1px solid #e1e1e1;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
    position: sticky;
    top: 0;
    z-index: 1000;
    margin-bottom: 20px;
}

.nav-container {
    max-width: 1400px;
    margin: 0 auto;
    padding: 0 20px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 60px;
}

.nav-brand {
    display: flex;
    align-items: center;
}

.nav-brand-link {
    text-decoration: none;
    color: inherit;
    display: flex;
    align-items: center;
    transition: opacity 0.2s ease;
}

.nav-brand-link:hover {
    opacity: 0.8;
}

.nav-brand-text {
    font-size: 1.25rem;
    font-weight: 600;
    color: #0078d4;
}

.nav-menu {
    display: flex;
    list-style: none;
    margin: 0;
    padding: 0;
    gap: 8px;
}

.nav-item {
    position: relative;
}

.nav-link {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 16px;
    text-decoration: none;
    color: #323130;
    border-radius: 4px;
    transition: all 0.2s ease;
    font-size: 0.9rem;
    font-weight: 500;
    white-space: nowrap;
}

.nav-link:hover {
    background-color: #f3f2f1;
    color: #0078d4;
}

.nav-link.active {
    background-color: #0078d4;
    color: #ffffff;
}

.nav-icon {
    font-size: 1rem;
    line-height: 1;
}

.container {
    max-width: 1400px;
    margin: 20px auto;
    padding: 20px;
    background-color: #ffffff;
    border-radius: 8px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.1);
    position: relative;
    overflow: hidden;
}

.container::before {
    content: '';
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 4px;
    background: linear-gradient(90deg, #0078d4 0%, #40e0d0 100%);
}

h1 {
    color: #323130;
    font-size: 2rem;
    font-weight: 600;
    margin: 0 0 20px 0;
    border-bottom: 2px solid #0078d4;
    padding-bottom: 10px;
}

.search-container {
    display: flex;
    align-items: center;
    margin-bottom: 20px;
    gap: 12px;
}

#searchInput {
    flex: 1;
    min-width: 300px;
    padding: 12px 16px;
    border: 2px solid #e1e1e1;
    border-radius: 4px;
    font-size: 1rem;
    transition: border-color 0.2s ease;
}

#searchInput:focus {
    outline: none;
    border-color: #0078d4;
    box-shadow: 0 0 0 1px #0078d4;
}

.filter-container {
    margin-bottom: 24px;
}

.filter-buttons {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
}

.filter-btn {
    padding: 8px 16px;
    border: 2px solid #e1e1e1;
    background-color: #ffffff;
    color: #323130;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.9rem;
    font-weight: 500;
    transition: all 0.2s ease;
}

.filter-btn:hover {
    border-color: #0078d4;
    color: #0078d4;
}

.filter-btn.active {
    background-color: #0078d4;
    border-color: #0078d4;
    color: #ffffff;
}

.clear-btn {
    background-color: #d13438;
    border-color: #d13438;
    color: #ffffff;
}

.clear-btn:hover {
    background-color: #a4262c;
    border-color: #a4262c;
}

table {
    width: 100%;
    border-collapse: collapse;
    margin-top: 20px;
    font-size: 0.9rem;
    background-color: #ffffff;
    border-radius: 4px;
    overflow: hidden;
    box-shadow: 0 1px 3px rgba(0,0,0,0.1);
}

thead {
    background: #0078d4;
}

th {
    padding: 16px 20px;
    text-align: left;
    border-bottom: 2px solid #e1dfdd;
    font-weight: 600;
    font-size: 0.9rem;
    letter-spacing: 0.5px;
    color: #ffffff !important;
    background: #0078d4;
}

td {
    padding: 16px 20px;
    border-bottom: 1px solid #f3f2f1;
    vertical-align: top;
}

tbody tr {
    transition: background-color 0.15s ease;
}

tbody tr:hover {
    background-color: #f8f9fa;
}

tbody tr:nth-child(even) {
    background-color: #fafbfc;
}

tbody tr:nth-child(even):hover {
    background-color: #f1f3f4;
}

/* Clean table styling without additional padding */
tbody {
    background-color: #ffffff;
}

.provider-name {
    font-weight: 600;
    color: #323130;
    font-size: 1rem;
}

.resource-types-list {
    list-style: none;
    padding: 0;
    margin: 0;
}

.resource-types-list li {
    display: inline-block;
    background-color: #f3f2f1;
    color: #323130;
    padding: 4px 8px;
    margin: 2px 4px 2px 0;
    border-radius: 4px;
    font-size: 0.8rem;
    border-left: 3px solid #d2d0ce;
}

.resource-types-list li.available-in-region {
    background-color: #dff6dd;
    color: #107c10;
    border-left-color: #107c10;
}

.available {
    background-color: #dff6dd;
    color: #107c10;
    padding: 6px 12px;
    border-radius: 4px;
    font-weight: 600;
    font-size: 0.85rem;
    text-align: center;
    display: inline-block;
}

.partial-available {
    background-color: #fff4ce;
    color: #8a8200;
    padding: 6px 12px;
    border-radius: 4px;
    font-weight: 600;
    font-size: 0.85rem;
    text-align: center;
    display: inline-block;
}

.not-available {
    background-color: #fde7e9;
    color: #d13438;
    padding: 6px 12px;
    border-radius: 4px;
    font-weight: 600;
    font-size: 0.85rem;
    text-align: center;
    display: inline-block;
}

.hero-section {
    text-align: center;
    padding: 40px 0;
    background: linear-gradient(135deg, #0078d4 0%, #40e0d0 100%);
    color: white;
    margin: -20px -20px 40px -20px;
    border-radius: 0 0 12px 12px;
}

.hero-title {
    font-size: 2.5rem;
    font-weight: 700;
    margin-bottom: 16px;
    text-shadow: 0 2px 4px rgba(0,0,0,0.3);
}

.hero-subtitle {
    font-size: 1.2rem;
    font-weight: 400;
    opacity: 0.9;
    max-width: 600px;
    margin: 0 auto;
}

.features-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: 24px;
    margin: 40px 0;
}

.feature-card {
    background: #ffffff;
    border: 1px solid #e1e1e1;
    border-radius: 8px;
    padding: 24px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.1);
    transition: all 0.3s ease;
    text-decoration: none;
    color: inherit;
}

.feature-card:hover {
    box-shadow: 0 4px 16px rgba(0,0,0,0.15);
    transform: translateY(-2px);
}

.feature-icon {
    font-size: 2rem;
    margin-bottom: 16px;
    display: block;
}

.feature-title {
    font-size: 1.2rem;
    font-weight: 600;
    margin-bottom: 12px;
    color: #0078d4;
}

.feature-description {
    color: #605e5c;
    line-height: 1.6;
}";
}

// Helper method to create index.html page
static async Task CreateIndexPage(BlobContainerClient containerClient, string targetRegion, dynamic resourceProviderData, string fileName)
{
    var providerCount = ((List<object>)resourceProviderData.resourceProviders).Count;
    var generatedTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    
    var indexHtml = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Azure Resources Dashboard - Home</title>
    <link rel=""stylesheet"" href=""azure_unified_styles.css"">
</head>
<body>
    <nav class=""main-nav"">
        <div class=""nav-container"">
            <div class=""nav-brand"">
                <a href=""index.html"" class=""nav-brand-link"">
                    <span class=""nav-brand-text"">Azure Resources Dashboard</span>
                </a>
            </div>
            <ul class=""nav-menu"">
                <li class=""nav-item"">
                    <a href=""index.html"" class=""nav-link active"" data-page=""home"">
                        <span class=""nav-icon"">🏠</span>
                        <span class=""nav-text"">Home</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""resources.html"" class=""nav-link"" data-page=""resources"">
                        <span class=""nav-icon"">📦</span>
                        <span class=""nav-text"">Resource Providers</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""vm-skus.html"" class=""nav-link"" data-page=""vm-skus"">
                        <span class=""nav-icon"">💻</span>
                        <span class=""nav-text"">VM SKUs</span>
                    </a>
                </li>

            </ul>
        </div>
    </nav>

    <div class=""container"">
        <div class=""hero-section"">
            <h1 class=""hero-title"">Azure Resources Dashboard</h1>
            <p class=""hero-subtitle"">Comprehensive view of Azure resource provider availability in {targetRegion}</p>
        </div>

        <div class=""features-grid"">
            <a href=""resources.html"" class=""feature-card"">
                <span class=""feature-icon"">📦</span>
                <h2 class=""feature-title"">Resource Providers</h2>
                <p class=""feature-description"">
                    Browse {providerCount} registered resource providers 
                    and their availability in {targetRegion}. View detailed resource types and availability zones.
                </p>
            </a>
            
            <a href=""vm-skus.html"" class=""feature-card"">
                <span class=""feature-icon"">💻</span>
                <h2 class=""feature-title"">VM SKUs</h2>
                <p class=""feature-description"">
                    Explore available virtual machine SKUs in {targetRegion}. 
                    View capabilities, families, and availability zone support for each VM size.
                </p>
            </a>

            <div class=""feature-card"">
                <span class=""feature-icon"">📄</span>
                <h2 class=""feature-title"">Raw JSON Data</h2>
                <p class=""feature-description"">
                    Access the complete datasets in JSON format for programmatic consumption and integration 
                    with your own applications and tools.
                </p>
                <div style=""margin-top: 15px;"">
                    <a href=""resource-providers.json"" style=""color: #0078d4; text-decoration: none; margin-right: 20px; display: inline-block; font-weight: 500;"">
                        📦 Resource Providers JSON
                    </a>
                    <a href=""vm-skus.json"" style=""color: #0078d4; text-decoration: none; display: inline-block; font-weight: 500;"">
                        💻 VM SKUs JSON
                    </a>
                </div>
            </div>

            <div class=""feature-card"">
                <span class=""feature-icon"">📊</span>
                <h2 class=""feature-title"">Region Info</h2>
                <p class=""feature-description"">
                    <strong>Region:</strong> {targetRegion}<br>
                    <strong>Generated:</strong> {generatedTime} UTC<br>
                    <strong>Resource Providers:</strong> {providerCount}
                </p>
            </div>
        </div>
    </div>
</body>
</html>";

    var indexBytes = Encoding.UTF8.GetBytes(indexHtml);
    var indexBlobClient = containerClient.GetBlobClient("index.html");
    
    using (var indexStream = new MemoryStream(indexBytes))
    {
        await indexBlobClient.UploadAsync(indexStream, overwrite: true);
        await indexBlobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "text/html"
        });
    }
    
    Console.WriteLine("✅ Successfully uploaded 'index.html'");
}

// Helper method to create resources.html page
static async Task CreateResourcesPage(BlobContainerClient containerClient, string targetRegion, dynamic resourceProviderData)
{
    var tableRows = new StringBuilder();
    var totalProviders = ((List<object>)resourceProviderData.resourceProviders).Count;
    var generatedTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    
    foreach (var provider in (List<object>)resourceProviderData.resourceProviders)
    {
        var providerObj = (dynamic)provider;
        var providerNamespace = providerObj.namespace_;
        var resourceTypes = (List<object>)providerObj.resourceTypes;
        var hasResourcesInRegion = (bool)providerObj.hasResourcesInRegion;
        
        // Count available vs total resource types
        var availableResourceTypes = resourceTypes.Count(rt => (bool)((dynamic)rt).availableInRegion);
        var totalResourceTypes = resourceTypes.Count;
        
        // Determine availability status
        string availability;
        string availabilityText;
        
        if (availableResourceTypes == 0)
        {
            availability = "not-available";
            availabilityText = "Not Available";
        }
        else if (availableResourceTypes == totalResourceTypes)
        {
            availability = "available";
            availabilityText = "Available";
        }
        else
        {
            availability = "partial-available";
            availabilityText = $"Partial ({availableResourceTypes}/{totalResourceTypes})";
        }
        
        // Build resource types list
        var resourceTypesHtml = new StringBuilder();
        resourceTypesHtml.Append("<ul class=\"resource-types-list\">");
        
        foreach (var resourceType in resourceTypes)
        {
            var rtObj = (dynamic)resourceType;
            var rtName = rtObj.name;
            var rtAvailable = (bool)rtObj.availableInRegion;
            var cssClass = rtAvailable ? "available-in-region" : "";
            resourceTypesHtml.Append($"<li class=\"{cssClass}\">{rtName}</li>");
        }
        
        resourceTypesHtml.Append("</ul>");
        
        tableRows.AppendLine($@"
                <tr data-availability=""{availability}"">
                    <td><span class=""provider-name"">{providerNamespace}</span></td>
                    <td>{resourceTypesHtml}</td>
                    <td><span class=""{availability}"">{availabilityText}</span></td>
                </tr>");
    }

    var resourcesHtml = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Azure Resource Providers - {targetRegion}</title>
    <link rel=""stylesheet"" href=""azure_unified_styles.css"">
</head>
<body>
    <nav class=""main-nav"">
        <div class=""nav-container"">
            <div class=""nav-brand"">
                <a href=""index.html"" class=""nav-brand-link"">
                    <span class=""nav-brand-text"">Azure Resources Dashboard</span>
                </a>
            </div>
            <ul class=""nav-menu"">
                <li class=""nav-item"">
                    <a href=""index.html"" class=""nav-link"" data-page=""home"">
                        <span class=""nav-icon"">🏠</span>
                        <span class=""nav-text"">Home</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""resources.html"" class=""nav-link active"" data-page=""resources"">
                        <span class=""nav-icon"">📦</span>
                        <span class=""nav-text"">Resource Providers</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""vm-skus.html"" class=""nav-link"" data-page=""vm-skus"">
                        <span class=""nav-icon"">💻</span>
                        <span class=""nav-text"">VM SKUs</span>
                    </a>
                </li>

            </ul>
        </div>
    </nav>
    
    <div class=""container"">
        <h1>Azure Resource Providers</h1>
        <p style=""color: #605e5c; font-size: 0.9rem; margin-bottom: 20px;"">
            <strong>Region:</strong> {targetRegion} | 
            <strong>Generated:</strong> {generatedTime} UTC |
            <strong>Total Providers:</strong> {totalProviders}
        </p>
        
        <div class=""search-container"">
            <input type=""text"" id=""searchInput"" placeholder=""Search resource providers, resource types, or availability..."">
        </div>
        
        <div class=""filter-container"">
            <div class=""filter-buttons"">
                <button id=""filterAvailable"" class=""filter-btn"">Available</button>
                <button id=""filterPartialAvailable"" class=""filter-btn"">Partial Available</button>
                <button id=""filterNotAvailable"" class=""filter-btn"">Not Available</button>
                <button id=""clearFilter"" class=""filter-btn clear-btn"">Clear Filter</button>
            </div>
        </div>
        
        <table id=""resourceTable"">
            <thead>
                <tr>
                    <th>Resource Provider</th>
                    <th>Resource Types</th>
                    <th>Available in {targetRegion}</th>
                </tr>
            </thead>
            <tbody>
                {tableRows}
            </tbody>
        </table>
    </div>

    <script>
    document.addEventListener('DOMContentLoaded', function() {{
        // Search functionality
        const searchInput = document.getElementById('searchInput');
        const table = document.getElementById('resourceTable');
        const rows = table.querySelectorAll('tbody tr');
        

        
        // Filter functionality
        const filterAvailable = document.getElementById('filterAvailable');
        const filterPartialAvailable = document.getElementById('filterPartialAvailable');
        const filterNotAvailable = document.getElementById('filterNotAvailable');
        const clearFilter = document.getElementById('clearFilter');
        
        let currentFilter = null;
        
        function applyFiltersAndSearch() {{
            const searchTerm = searchInput.value.toLowerCase();
            
            rows.forEach(row => {{
                const text = row.textContent.toLowerCase();
                const matchesSearch = !searchTerm || text.includes(searchTerm);
                const matchesFilter = !currentFilter || row.dataset.availability === currentFilter;
                
                row.style.display = (matchesSearch && matchesFilter) ? '' : 'none';
            }});
        }}
        
        function clearFilters() {{
            document.querySelectorAll('.filter-btn').forEach(btn => btn.classList.remove('active'));
            currentFilter = null;
            searchInput.value = '';
            applyFiltersAndSearch();
        }}
        
        function setFilter(filterType, buttonElement) {{
            document.querySelectorAll('.filter-btn').forEach(btn => btn.classList.remove('active'));
            buttonElement.classList.add('active');
            currentFilter = filterType;
            applyFiltersAndSearch();
        }}
        
        filterAvailable.addEventListener('click', function() {{
            setFilter('available', this);
        }});
        
        filterPartialAvailable.addEventListener('click', function() {{
            setFilter('partial-available', this);
        }});
        
        filterNotAvailable.addEventListener('click', function() {{
            setFilter('not-available', this);
        }});
        
        clearFilter.addEventListener('click', function() {{
            clearFilters();
        }});
        
        // Update search to work with filters
        searchInput.addEventListener('input', applyFiltersAndSearch);
    }});
    </script>
</body>
</html>";

    var resourcesBytes = Encoding.UTF8.GetBytes(resourcesHtml);
    var resourcesBlobClient = containerClient.GetBlobClient("resources.html");
    
    using (var resourcesStream = new MemoryStream(resourcesBytes))
    {
        await resourcesBlobClient.UploadAsync(resourcesStream, overwrite: true);
        await resourcesBlobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "text/html"
        });
    }
    
    Console.WriteLine("✅ Successfully uploaded 'resources.html'");
}

// Helper method to collect VM SKU data for the target region using Azure REST API
static async Task<List<object>> CollectVmSkuData(SubscriptionResource subscription, string targetRegion)
{
    var vmSkus = new List<object>();
    
    try
    {
        Console.WriteLine($"🔍 Collecting actual VM SKU data from Azure region: {targetRegion}");
        
        // Get access token using managed identity
        var accessToken = await GetAccessToken();
        
        // Use REST API to get VM SKU data with retry logic for rate limiting
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        httpClient.Timeout = TimeSpan.FromMinutes(5); // Increase timeout for large responses
        
        var subscriptionId = subscription.Id.SubscriptionId;
        var requestUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{targetRegion}'";
        
        Console.WriteLine($"📡 Making REST API call to: {requestUrl}");
        
        // Implement retry logic for rate limiting
        var maxRetries = 3;
        var baseDelay = TimeSpan.FromSeconds(2);
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await httpClient.GetAsync(requestUrl);
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * baseDelay.TotalSeconds);
                        Console.WriteLine($"⏳ Rate limited, waiting {delay.TotalSeconds} seconds before retry {attempt + 1}/{maxRetries}");
                        await Task.Delay(delay);
                        continue;
                    }
                    throw new HttpRequestException($"API rate limit exceeded after {maxRetries} attempts");
                }
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API request failed: {response.StatusCode} - {response.ReasonPhrase}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error details: {errorContent}");
                    return vmSkus;
                }
                
                var jsonContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"📊 API returned {jsonContent.Length} characters of data");
                
                // Parse the JSON response
                using var document = JsonDocument.Parse(jsonContent);
                var valueProperty = document.RootElement.GetProperty("value");

                foreach (var skuElement in valueProperty.EnumerateArray())
                {
                    if (!skuElement.TryGetProperty("name", out var nameProperty))
                        continue;

                    var skuName = nameProperty.GetString();
                    if (string.IsNullOrEmpty(skuName))
                        continue;
                    
                    // Filter for virtual machine SKUs only
                    if (!skuElement.TryGetProperty("resourceType", out var resourceTypeProperty) ||
                        resourceTypeProperty.GetString() != "virtualMachines")
                        continue;

                    // Extract family from SKU name (e.g., "Standard_D2s_v5" -> family="Dsv5", size="D2s_v5")
                    var family = ExtractVmFamily(skuName);
                    var size = skuName.StartsWith("Standard_") ? skuName.Substring(9) : skuName;

                    // Get ALL capabilities from the API
                    var capabilities = new List<object>();
                    var vcpus = "Unknown";
                    var memoryGB = "Unknown";
                    
                    if (skuElement.TryGetProperty("capabilities", out var capabilitiesArray))
                    {
                        foreach (var capability in capabilitiesArray.EnumerateArray())
                        {
                            if (capability.TryGetProperty("name", out var capName) && 
                                capability.TryGetProperty("value", out var capValue))
                            {
                                var capNameStr = capName.GetString();
                                var capValueStr = capValue.GetString();
                                
                                capabilities.Add(new { name = capNameStr, value = capValueStr });
                                
                                // Extract specific values for display
                                if (capNameStr == "vCPUs")
                                    vcpus = capValueStr;
                                else if (capNameStr == "MemoryGB")
                                    memoryGB = capValueStr;
                            }
                        }
                    }

                    // Get availability zones
                    var availableZones = new List<string>();
                    if (skuElement.TryGetProperty("locationInfo", out var locationInfoArray))
                    {
                        foreach (var locationInfo in locationInfoArray.EnumerateArray())
                        {
                            if (locationInfo.TryGetProperty("location", out var loc) && 
                                loc.GetString()?.Equals(targetRegion, StringComparison.OrdinalIgnoreCase) == true &&
                                locationInfo.TryGetProperty("zones", out var zonesArray))
                            {
                                foreach (var zone in zonesArray.EnumerateArray())
                                {
                                    var zoneStr = zone.GetString();
                                    if (!string.IsNullOrEmpty(zoneStr))
                                        availableZones.Add(zoneStr);
                                }
                                break;
                            }
                        }
                    }

                    // Determine availability status
                    string availabilityStatus;
                    if (availableZones.Count == 0)
                    {
                        availabilityStatus = "Available (No Zone Info)";
                    }
                    else if (availableZones.Count >= 3)
                    {
                        availabilityStatus = "Available In All Zones";
                    }
                    else
                    {
                        availabilityStatus = $"Available In {availableZones.Count} Zone(s)";
                    }

                    vmSkus.Add(new
                    {
                        name = skuName,
                        family = family,
                        size = size,
                        tier = "Standard",
                        capabilities = capabilities,
                        availabilityStatus = availabilityStatus,
                        availableZones = availableZones,
                        locations = new List<string> { targetRegion }
                    });
                }
                
                Console.WriteLine($"✅ Successfully collected {vmSkus.Count} actual VM SKUs from {targetRegion}");
                break; // Success, exit retry loop
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // Will retry
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Error collecting VM SKU data: {ex.Message}");
    }
    
    // Order by family first, then by CPU count (extracted from capabilities)
    return vmSkus.OrderBy(s => {
        var sku = (dynamic)s;
        return sku.family?.ToString() ?? "Unknown";
    }).ThenBy(s => {
        var sku = (dynamic)s;
        // Extract vCPU count from capabilities for sorting
        if (sku.capabilities != null)
        {
            foreach (var cap in sku.capabilities)
            {
                var capDynamic = (dynamic)cap;
                if (capDynamic.name?.ToString() == "vCPUs")
                {
                    if (int.TryParse(capDynamic.value?.ToString(), out int vcpuCount))
                        return vcpuCount;
                }
            }
        }
        return 0; // Default if no vCPU count found
    }).ThenBy(s => ((dynamic)s).name).ToList();
}

// Helper method to upload VM SKU JSON data
static async Task UploadVmSkuJson(BlobContainerClient containerClient, List<object> vmSkuData, string targetRegion)
{
    var vmSkuDocument = new
    {
        region = targetRegion,
        generatedAt = DateTime.UtcNow,
        vmSkuCount = vmSkuData.Count,
        vmSkus = vmSkuData
    };
    
    var jsonOptions = new JsonSerializerOptions 
    { 
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    var jsonContent = JsonSerializer.Serialize(vmSkuDocument, jsonOptions);
    var contentBytes = Encoding.UTF8.GetBytes(jsonContent);
    
    var fileName = "vm-skus.json";
    var blobClient = containerClient.GetBlobClient(fileName);
    
    using (var stream = new MemoryStream(contentBytes))
    {
        await blobClient.UploadAsync(stream, overwrite: true);
        
        await blobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "application/json"
        });
    }

    Console.WriteLine($"✅ Successfully uploaded '{fileName}' to storage account");
    Console.WriteLine($"📍 VM SKU Blob URL: {blobClient.Uri}");
}

// Helper method to create vm-skus.html page
static async Task CreateVmSkusPage(BlobContainerClient containerClient, string targetRegion, List<object> vmSkuData)
{
    var generatedTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
    var totalSkus = vmSkuData.Count;
    
    // Count availability statistics
    var availableSkus = vmSkuData.Count(s => 
    {
        var status = ((dynamic)s).availabilityStatus.ToString();
        return status.Contains("Available") && !status.Contains("Not Available");
    });
    var notAvailableSkus = vmSkuData.Count(s => 
        ((dynamic)s).availabilityStatus.ToString().Contains("Not Available"));
    
    var vmSkusHtml = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>VM SKUs - Azure Resource Dashboard</title>
    <link href=""azure_unified_styles.css"" rel=""stylesheet"">
</head>
<body>
    <nav class=""main-nav"">
        <div class=""nav-container"">
            <div class=""nav-brand"">
                <a href=""index.html"" class=""nav-brand-link"">
                    <span class=""nav-brand-text"">Azure Resource Dashboard</span>
                </a>
            </div>
            <ul class=""nav-menu"">
                <li class=""nav-item"">
                    <a href=""index.html"" class=""nav-link"" data-page=""home"">
                        <span class=""nav-icon"">🏠</span>
                        <span class=""nav-text"">Home</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""resources.html"" class=""nav-link"" data-page=""resources"">
                        <span class=""nav-icon"">📦</span>
                        <span class=""nav-text"">Resource Providers</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""vm-skus.html"" class=""nav-link active"" data-page=""vm-skus"">
                        <span class=""nav-icon"">💻</span>
                        <span class=""nav-text"">VM SKUs</span>
                    </a>
                </li>

            </ul>
        </div>
    </nav>
    
    <div class=""container"">
        <h1>Azure VM SKUs</h1>
        <p style=""color: #605e5c; font-size: 0.9rem; margin-bottom: 20px;"">
            <strong>Region:</strong> {targetRegion} | 
            <strong>Generated:</strong> {generatedTime} UTC |
            <strong>Total SKUs:</strong> {totalSkus} |
            <strong>Available:</strong> {availableSkus} |
            <strong>Not Available:</strong> {notAvailableSkus}
        </p>
        
        <div class=""search-container"">
            <input type=""text"" id=""searchInput"" placeholder=""Search VM SKUs, capabilities, or availability..."">
        </div>
        
        <div class=""filter-container"">
            <div class=""filter-buttons"">
                <button id=""filterAvailable"" class=""filter-btn"">Available In All Zones</button>
                <button id=""filterPartialAvailable"" class=""filter-btn"">Available In Some Zones</button>
                <button id=""filterNotAvailable"" class=""filter-btn"">Not Available</button>
                <button id=""clearFilter"" class=""filter-btn clear-btn"">Clear Filter</button>
            </div>
        </div>
        
        <div class=""table-container"">
            <table id=""vmSkuTable"" class=""resource-table"">
                <thead>
                    <tr>
                        <th>VM SKU</th>
                        <th>Family</th>
                        <th>Size</th>
                        <th>All Capabilities</th>
                        <th>Availability in {targetRegion}</th>
                    </tr>
                </thead>
                <tbody>";

    foreach (var sku in vmSkuData)
    {
        var skuDynamic = (dynamic)sku;
        var skuName = skuDynamic.name?.ToString() ?? "";
        var family = skuDynamic.family?.ToString() ?? "";
        var size = skuDynamic.size?.ToString() ?? "";
        var availabilityStatus = skuDynamic.availabilityStatus?.ToString() ?? "Not Available";
        
        // Determine availability class for styling
        string availabilityClass;
        if (availabilityStatus.Contains("Available In All Zones"))
        {
            availabilityClass = "available";
        }
        else if (availabilityStatus.Contains("Available") && !availabilityStatus.Contains("Not Available"))
        {
            availabilityClass = "partial-available";
        }
        else
        {
            availabilityClass = "not-available";
        }
        
        // Extract all capabilities
        var allCapabilities = new List<string>();
        if (skuDynamic.capabilities != null)
        {
            foreach (var cap in skuDynamic.capabilities)
            {
                var capDynamic = (dynamic)cap;
                var capName = capDynamic.name?.ToString() ?? "";
                var capValue = capDynamic.value?.ToString() ?? "";
                
                if (!string.IsNullOrEmpty(capName) && !string.IsNullOrEmpty(capValue))
                {
                    allCapabilities.Add($"{capName}: {capValue}");
                }
            }
        }
        
        // Format capabilities for better readability with line breaks
        var capabilitiesText = allCapabilities.Any() ? 
            string.Join("<br/>", allCapabilities) : "No capabilities available";
        
        vmSkusHtml += $@"
                    <tr data-availability=""{availabilityClass}"">
                        <td><strong>{skuName}</strong></td>
                        <td>{family}</td>
                        <td>{size}</td>
                        <td><small style=""line-height: 1.4;"">{capabilitiesText}</small></td>
                        <td><span class=""status-badge {availabilityClass}"">{availabilityStatus}</span></td>
                    </tr>";
    }

    vmSkusHtml += @"
                </tbody>
            </table>
        </div>
    </div>

    <script>
    document.addEventListener('DOMContentLoaded', function() {
        const searchInput = document.getElementById('searchInput');
        const table = document.getElementById('vmSkuTable');
        const rows = table.querySelectorAll('tbody tr');
        
        // Filter functionality
        const filterAvailable = document.getElementById('filterAvailable');
        const filterPartialAvailable = document.getElementById('filterPartialAvailable');
        const filterNotAvailable = document.getElementById('filterNotAvailable');
        const clearFilter = document.getElementById('clearFilter');
        
        let currentFilter = null;
        
        function applyFiltersAndSearch() {
            const searchTerm = searchInput.value.toLowerCase();
            
            rows.forEach(row => {
                const text = row.textContent.toLowerCase();
                const matchesSearch = !searchTerm || text.includes(searchTerm);
                const matchesFilter = !currentFilter || row.dataset.availability === currentFilter;
                
                row.style.display = (matchesSearch && matchesFilter) ? '' : 'none';
            });
        }
        
        function clearFilters() {
            document.querySelectorAll('.filter-btn').forEach(btn => btn.classList.remove('active'));
            currentFilter = null;
            searchInput.value = '';
            applyFiltersAndSearch();
        }
        
        function setFilter(filterType, buttonElement) {
            document.querySelectorAll('.filter-btn').forEach(btn => btn.classList.remove('active'));
            buttonElement.classList.add('active');
            currentFilter = filterType;
            applyFiltersAndSearch();
        }
        
        filterAvailable.addEventListener('click', function() {
            setFilter('available', this);
        });
        
        filterPartialAvailable.addEventListener('click', function() {
            setFilter('partial-available', this);
        });
        
        filterNotAvailable.addEventListener('click', function() {
            setFilter('not-available', this);
        });
        
        clearFilter.addEventListener('click', function() {
            clearFilters();
        });
        
        // Update search to work with filters
        searchInput.addEventListener('input', applyFiltersAndSearch);
    });
    </script>
</body>
</html>";

    var vmSkuBytes = Encoding.UTF8.GetBytes(vmSkusHtml);
    var vmSkuBlobClient = containerClient.GetBlobClient("vm-skus.html");
    
    using (var vmSkuStream = new MemoryStream(vmSkuBytes))
    {
        await vmSkuBlobClient.UploadAsync(vmSkuStream, overwrite: true);
        await vmSkuBlobClient.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "text/html"
        });
    }
    
    Console.WriteLine("✅ Successfully uploaded 'vm-skus.html'");
}

// Helper method to get access token using managed identity
static async Task<string> GetAccessToken()
{
    Console.WriteLine("🔐 Getting access token using managed identity...");
    var credential = new DefaultAzureCredential();
    var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
    var token = await credential.GetTokenAsync(tokenRequestContext);
    Console.WriteLine("✅ Successfully obtained access token using managed identity");
    return token.Token;
}

// Helper method to extract VM family from SKU name
static string ExtractVmFamily(string skuName)
{
    if (string.IsNullOrEmpty(skuName))
        return "Unknown";
    
    // Handle Standard_ prefix
    var name = skuName.StartsWith("Standard_") ? skuName.Substring(9) : skuName;
    
    // Extract family patterns (e.g., "D2s_v5" -> "Dsv5", "B1ms" -> "B")
    if (name.Contains("_v"))
    {
        var parts = name.Split('_');
        if (parts.Length >= 2)
        {
            var basePart = parts[0]; // e.g., "D2s"
            var versionPart = parts[1]; // e.g., "v5"
            
            // Extract letters and 's' from base part
            var family = new string(basePart.Where(c => char.IsLetter(c)).ToArray());
            return family + versionPart;
        }
    }
    
    // For names without version (e.g., "B1ms")
    return new string(name.Where(c => char.IsLetter(c)).ToArray());
}