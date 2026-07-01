// Resource-group-scoped IaC for the LTL tool's UAT infrastructure on Azure App Service.
// Ported from the freight-dna sibling repo's UAT provisioning pattern (same resource shape:
// Container Registry + App Service Plan + two container Web Apps + Log Analytics + App Insights)
// with an Azure SQL logical server + database added, since this API requires one to run.
//
// Entra ID app registrations are NOT modeled here: Bicep/ARM cannot manage Microsoft Entra
// objects (they are Microsoft Graph resources, not ARM resources). Those are created by az cli
// steps in .github/workflows/provision-ltl-uat-infra.yml, which calls this template for
// everything ARM can own.
//
// Deploy at resource-group scope: the pipeline creates/ensures the resource group first
// (az group create), then runs `az deployment group create -f infra/uat/main.bicep`.

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Base name for all resources (e.g. ltl-uat). Kept short — feeds the ACR name too.')
param baseName string = 'ltl-uat'

@description('SQL Server administrator login name.')
param sqlAdminLogin string = 'ltlsqladmin'

@description('SQL Server administrator password. Pass via a GitHub secret — never commit a value.')
@secure()
param sqlAdminPassword string

@description('Azure SQL Database SKU name (e.g. Basic, S0, S1).')
param sqlDatabaseSkuName string = 'S0'

var cleanName = replace(baseName, '-', '')
var planName = '${baseName}-plan'
var webAppName = '${baseName}-web'
var apiAppName = '${baseName}-api'
var acrName = take('${cleanName}acr', 50)
var workspaceName = '${baseName}-law'
var appInsightsName = '${baseName}-ai'
var sqlServerName = '${baseName}-sql-${uniqueString(resourceGroup().id)}'
var sqlDatabaseName = '${baseName}-sqldb'
var tags = {
  project: 'LtlTool'
  environment: 'uat'
  owner: 'ltl-uat-github-deploy'
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: workspace.id }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: true }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
  }
}

// App Service has no static egress IP range without VNet integration, so the server allows
// Azure-internal traffic. Tighten this with a VNet + private endpoint before this environment
// carries production data.
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: { name: sqlDatabaseSkuName }
  properties: { zoneRedundant: false }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  kind: 'linux'
  properties: { reserved: true }
}

// Placeholder image — scripts/deploy-azure-uat.sh builds the real API image into the ACR above
// and repoints this app at it via `az webapp config container set` on every deploy.
resource apiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
  location: location
  tags: tags
  kind: 'app,linux,container'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|mcr.microsoft.com/dotnet/aspnet:10.0'
      alwaysOn: true
      appSettings: [
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'UAT' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// Placeholder image — scripts/deploy-azure-uat.sh builds the real web image (nginx + the Angular
// production build) into the ACR above and repoints this app at it on every deploy.
resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux,container'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|mcr.microsoft.com/library/nginx:1.27-alpine'
      alwaysOn: true
      appSettings: [
        { name: 'WEBSITES_PORT', value: '80' }
        { name: 'API_UPSTREAM', value: 'https://${apiAppName}.azurewebsites.net' }
      ]
    }
  }
}

output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output apiAppUrl string = 'https://${apiApp.properties.defaultHostName}'
output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
output logAnalyticsWorkspaceId string = workspace.id
