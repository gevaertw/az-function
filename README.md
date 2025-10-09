# Azure Resource Checker

## Functionality

The Azure Resource Checker is an automated system that provides comprehensive visibility into Azure service availability and virtual machine capabilities in specific Azure regions. The system continuously monitors and reports on:

**Resource Provider Analysis**
- Displays all Azure resource providers and their availability status in the target region
- Shows which services are fully available, partially available, or not available
- Provides detailed resource type information for each provider

**VM SKU Discovery**
- Lists all virtual machine sizes (SKUs) available in the target region
- Shows complete capability information including vCPUs, memory, storage, and networking features
- Displays availability zone support for each VM SKU
- Organizes VMs by family and CPU count for easy comparison

**Interactive Web Dashboard**
- Clean, responsive web interface with Azure-themed styling
- Advanced filtering and search capabilities across all data
- Real-time data updates based on scheduled collection intervals
- Direct access to raw JSON data for programmatic integration

## Technical Solution

**Architecture**
The solution uses Azure Container App Jobs for scheduled execution, eliminating the need for always-running infrastructure while ensuring regular data updates. The system authenticates using managed identity and stores results in Azure Storage with static website hosting enabled.

**Core Components**
- **.NET 9 Application**: Collects data via Azure Resource Manager REST APIs
- **Azure Container App Jobs**: Provides scheduled execution every configurable interval (default: 30 minutes)
- **Azure Storage Account**: Hosts static website and stores JSON data files
- **Bicep Templates**: Infrastructure as Code for consistent deployments
- **Managed Identity**: Secure authentication without stored credentials

**Data Collection**
The application queries Azure Resource Manager APIs to gather real-time information about:
- Resource provider registration and availability status
- VM SKU specifications and zone availability
- Service capabilities and limitations in the target region

**Security & Authentication**
- User-assigned managed identity with Reader permissions at subscription scope
- No secrets or connection strings stored in code or configuration
- RBAC-based access control for all Azure resources
- Secure REST API communication with bearer token authentication

**Deployment**
Infrastructure deployment uses Bicep templates with configurable parameters stored in `parameters.json`. The system supports different Azure regions through the `targetRegion` parameter and adjustable collection schedules via `scheduleIntervalMinutes`.

**Usage**
Deploy the solution using `./deploy.sh` after configuring parameters. The system automatically starts collecting data based on the configured schedule and publishes results to the static website endpoint.

## Configuration Parameters and Secrets

**Parameters File (`parameters.json`)**
The deployment uses a parameters file to configure all aspects of the infrastructure and application behavior:

- **deployName**: Unique identifier for the deployment, used as prefix for resource naming
- **namePrefix**: Short prefix added to all Azure resource names for consistency
- **location**: Azure region where infrastructure resources will be deployed
- **targetRegion**: Azure region to analyze for resource provider and VM SKU availability
- **rgname**: Name of the Azure resource group to create or use for all resources
- **containerImage**: Full URI of the container image in Azure Container Registry
- **scheduleIntervalMinutes**: Interval in minutes between scheduled data collection runs

**Secrets File (`secrets.json`)**
Contains sensitive configuration values that should not be stored in source control:

- **subscriptionId**: Azure subscription ID where resources will be deployed
- **clientId**: Application (client) ID of the Azure AD application used for authentication
- **tenantId**: Azure Active Directory tenant ID for the subscription

**Environment Variables**
The containerized application receives configuration through environment variables automatically set by the Container App Job:

- **AZURE_CLIENT_ID**: Managed identity client ID for Azure authentication
- **STORAGE_ACCOUNT_NAME**: Name of the Azure Storage Account for file uploads
- **CONTAINER_NAME**: Storage container name (typically '$web' for static websites)
- **TARGET_REGION**: Azure region to query for resource availability data

**Security Considerations**
- All sensitive values should be stored in `secrets.json` and excluded from version control
- The `parameters.json` file can be safely committed as it contains no sensitive information
- Managed identity authentication eliminates the need for storing connection strings or API keys
- RBAC permissions are automatically configured during deployment with minimal required access

