using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.Storage.Blobs;
using System.Text;
using System.Text.Json;

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
            // Skip if provider is not registered
            if (provider.Data.RegistrationState != "Registered")
                continue;

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
    
    // Upload CSS file
    Console.WriteLine("🎨 Uploading CSS styles...");
    await UploadCssFile(containerClient);
    
    // Create index.html file with proper styling and navigation
    Console.WriteLine("🏠 Generating index.html...");
    await CreateIndexPage(containerClient, targetRegion, resourceProviderData, fileName);
    
    // Create resources.html file with resource provider table
    Console.WriteLine("📊 Generating resources.html...");
    await CreateResourcesPage(containerClient, targetRegion, resourceProviderData);

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
                        <span class=""nav-icon"">🏢</span>
                        <span class=""nav-text"">Resource Providers</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""{fileName}"" class=""nav-link"" data-page=""json"">
                        <span class=""nav-icon"">📄</span>
                        <span class=""nav-text"">Raw JSON</span>
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
                <span class=""feature-icon"">🏢</span>
                <h2 class=""feature-title"">Resource Providers</h2>
                <p class=""feature-description"">
                    Browse {providerCount} registered resource providers 
                    and their availability in {targetRegion}. View detailed resource types and availability zones.
                </p>
            </a>

            <a href=""{fileName}"" class=""feature-card"">
                <span class=""feature-icon"">📄</span>
                <h2 class=""feature-title"">Raw JSON Data</h2>
                <p class=""feature-description"">
                    Access the complete dataset in JSON format for programmatic consumption and integration 
                    with your own applications and tools.
                </p>
            </a>

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
                        <span class=""nav-icon"">🏢</span>
                        <span class=""nav-text"">Resource Providers</span>
                    </a>
                </li>
                <li class=""nav-item"">
                    <a href=""resource-providers.json"" class=""nav-link"" data-page=""json"">
                        <span class=""nav-icon"">📄</span>
                        <span class=""nav-text"">Raw JSON</span>
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