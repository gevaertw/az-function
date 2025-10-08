# Multi-stage build for .NET Console App
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY FileWriter.csproj .
RUN dotnet restore

# Copy source code and build
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Set the entry point
ENTRYPOINT ["dotnet", "FileWriter.dll"]