// Resource-group-scoped IaC for the LTL tool's shared Azure UAT infrastructure.
//
// Deliberately scoped to what is NOT already automated by
// .github/workflows/deploy-ghcr-azure-container-apps.yml:
//   - Log Analytics workspace (required by the Container Apps environment)
//   - Container Apps environment (the deploy workflow only creates the two Container
//     Apps themselves; it already creates the environment imperatively via `az containerapp
//     env create`, so this template's environment resource is idempotent with that step —
//     either can run first)
//   - Azure SQL logical server + database
//
// Entra ID app registrations are NOT modeled here: Bicep/ARM cannot manage Microsoft Entra
// objects (they are Microsoft Graph resources, not ARM resources). Those are created by
// az cli steps in .github/workflows/provision-azure-resources.yml, which calls this template
// for everything ARM can own.
//
// All resource names are prefixed `ltl-uat-` per naming convention. Deploy at resource-group
// scope: the pipeline creates/ensures the resource group first (az group create), then runs
// `az deployment group create -f infra/main.bicep`.

@description('Short environment label used in resource names (e.g. uat).')
param environmentName string = 'uat'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('SQL Server administrator login name.')
param sqlAdminLogin string = 'ltlsqladmin'

@description('SQL Server administrator password. Pass via a GitHub secret — never commit a value.')
@secure()
param sqlAdminPassword string

@description('Azure SQL Database SKU name (e.g. Basic, S0, S1).')
param sqlDatabaseSkuName string = 'S0'

@description('Container Apps environment name.')
param containerAppsEnvironmentName string = 'ltl-uat-cae'

var namePrefix = 'ltl-${environmentName}'
var logAnalyticsName = '${namePrefix}-law'
var sqlServerName = '${namePrefix}-sql-${uniqueString(resourceGroup().id)}'
var sqlDatabaseName = '${namePrefix}-sqldb'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppsEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
  }
}

// Azure Container Apps has no static egress IP range without VNet integration, so the
// server allows Azure-internal traffic. Tighten this with a VNet + private endpoint before
// this environment carries production data.
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
  sku: {
    name: sqlDatabaseSkuName
  }
  properties: {
    zoneRedundant: false
  }
}

output containerAppsEnvironmentId string = containerAppsEnvironment.id
output containerAppsEnvironmentName string = containerAppsEnvironment.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
output logAnalyticsWorkspaceId string = logAnalytics.id
