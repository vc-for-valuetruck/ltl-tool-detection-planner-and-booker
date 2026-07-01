// LTL Tool Detection, Planner, and Booker — Azure hosting infrastructure.
//
// Target architecture (see docs/AZURE_HOSTING.md for the rationale):
//   - API  -> Azure Container Apps (containerized .NET 10 API, scales 0..N)
//   - Web  -> Azure Container Apps (nginx static SPA + /api reverse proxy)
//   - Data -> Azure SQL Database (EF Core saved views + operation outbox)
//   - Secrets -> Azure Key Vault, read by the API app via a user-assigned
//                managed identity (no secrets baked into images or this file)
//   - Logs -> Log Analytics workspace backing the Container Apps environment
//
// This template is reviewable scaffolding. It provisions the resources and wires
// configuration, but every credential is a @secure() parameter supplied at deploy
// time by the pipeline / operator — nothing secret is committed. Deploy with:
//   az deployment group create -g <rg> -f infra/main.bicep -p @infra/main.parameters.json
//
// Container images are expected in GitHub Container Registry (GHCR); the deploy
// workflow (.github/workflows/deploy-ghcr-azure-container-apps.yml) builds/pushes
// them. This Bicep is an alternative, declarative path to the same topology.

targetScope = 'resourceGroup'

@description('Prefix applied to every resource name, e.g. "ltl-tool-uat".')
param namePrefix string = 'ltl-tool-uat'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Container image for the API, e.g. ghcr.io/<owner>/ltl-tool-detection-planner-and-booker-api:<tag>.')
param apiImage string

@description('Container image for the web SPA, e.g. ghcr.io/<owner>/ltl-tool-detection-planner-and-booker-web:<tag>.')
param webImage string

@description('Container registry login server (default GHCR).')
param registryServer string = 'ghcr.io'

@description('Container registry username (GitHub actor or PAT owner).')
param registryUsername string

@description('Container registry password / token. Supply at deploy time; never commit.')
@secure()
param registryPassword string

// ---- Application configuration (non-secret) ----
@description('Microsoft Entra instance authority.')
param entraInstance string = 'https://login.microsoftonline.com/'

@description('Comma-free single allowed email domain for the access policy (e.g. valuetruck.com). Empty allows any authenticated user.')
param allowedEmailDomain string = ''

@description('Alvys provider: Live or Fallback. Fallback returns empty results with no tenant.')
@allowed([
  'Live'
  'Fallback'
])
param alvysProvider string = 'Fallback'

param alvysApiBaseUrl string = 'https://integrations.alvys.com'
param alvysApiVersion string = 'v1'

@description('Alvys writeback mode. Disabled is the safe default and never mutates Alvys.')
@allowed([
  'Disabled'
  'Simulation'
  'Sandbox'
])
param alvysWritebackMode string = 'Disabled'

param ltlDetectionEnabled bool = false
param ltlDefaultTimezone string = 'America/Chicago'

// ---- Secrets (supplied at deploy time, stored in Key Vault) ----
@description('Entra tenant id used by the application.')
param entraTenantId string

@description('Entra API app registration client id.')
param entraApiClientId string

@description('Entra SPA (web) app registration client id.')
param entraWebClientId string

@description('Exposed API scope the SPA requests, e.g. api://<api-client-id>/access_as_user.')
param entraApiScope string = 'api://${entraApiClientId}/access_as_user'

@secure()
param entraApiClientSecret string

@description('SQL administrator login name.')
param sqlAdminLogin string

@secure()
param sqlAdminPassword string

@secure()
param alvysTenantId string = ''

@secure()
param alvysClientId string = ''

@secure()
param alvysClientSecret string = ''

// ---------------------------------------------------------------------------
// Naming
// ---------------------------------------------------------------------------
var sanitized = toLower(replace(namePrefix, '_', '-'))
var logName = '${sanitized}-logs'
var envName = '${sanitized}-env'
var identityName = '${sanitized}-id'
var keyVaultName = take('${replace(sanitized, '-', '')}kv${uniqueString(resourceGroup().id)}', 24)
var sqlServerName = '${sanitized}-sql-${uniqueString(resourceGroup().id)}'
var sqlDbName = 'LtlTool'
var apiAppName = '${sanitized}-api'
var webAppName = '${sanitized}-web'

// ---------------------------------------------------------------------------
// Identity + observability
// ---------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// ---------------------------------------------------------------------------
// Key Vault — holds every application secret; API reads via managed identity
// ---------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
  }
}

// Built-in "Key Vault Secrets User" role.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource sqlConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

resource entraClientSecretKv 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'azure-ad-client-secret'
  properties: {
    value: entraApiClientSecret
  }
}

resource alvysTenantSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'alvys-tenant-id'
  properties: {
    value: alvysTenantId
  }
}

resource alvysClientIdSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'alvys-client-id'
  properties: {
    value: alvysClientId
  }
}

resource alvysClientSecretKv 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'alvys-client-secret'
  properties: {
    value: alvysClientSecret
  }
}

// ---------------------------------------------------------------------------
// Azure SQL — managed SQL Server for EF Core saved views + operation records
// ---------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDbName
  location: location
  sku: {
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

// Allow other Azure services (incl. Container Apps egress) to reach SQL.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------------------------------------------------------------------------
// Container Apps environment
// ---------------------------------------------------------------------------
resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: envName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// ---------------------------------------------------------------------------
// API container app — secrets resolved from Key Vault via managed identity
// ---------------------------------------------------------------------------
resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: registryPassword
        }
        {
          name: 'sql-connection-string'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/sql-connection-string'
          identity: identity.id
        }
        {
          name: 'azure-ad-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/azure-ad-client-secret'
          identity: identity.id
        }
        {
          name: 'alvys-tenant-id'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/alvys-tenant-id'
          identity: identity.id
        }
        {
          name: 'alvys-client-id'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/alvys-client-id'
          identity: identity.id
        }
        {
          name: 'alvys-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/alvys-client-secret'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'AzureAd__Instance', value: entraInstance }
            { name: 'AzureAd__TenantId', value: entraTenantId }
            { name: 'AzureAd__ClientId', value: entraApiClientId }
            { name: 'AzureAd__Audience', value: 'api://${entraApiClientId}' }
            { name: 'AzureAd__ClientSecret', secretRef: 'azure-ad-client-secret' }
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'sql-connection-string' }
            { name: 'Cors__AllowedOrigins__0', value: 'https://${webAppName}.${containerEnv.properties.defaultDomain}' }
            { name: 'AccessPolicy__AllowedEmailDomains__0', value: allowedEmailDomain }
            { name: 'Alvys__Provider', value: alvysProvider }
            { name: 'Alvys__ApiBaseUrl', value: alvysApiBaseUrl }
            { name: 'Alvys__ApiVersion', value: alvysApiVersion }
            { name: 'Alvys__TenantId', secretRef: 'alvys-tenant-id' }
            { name: 'Alvys__ClientId', secretRef: 'alvys-client-id' }
            { name: 'Alvys__ClientSecret', secretRef: 'alvys-client-secret' }
            { name: 'Alvys__Writeback__Mode', value: alvysWritebackMode }
            { name: 'Ltl__DetectionEnabled', value: string(ltlDetectionEnabled) }
            { name: 'Ltl__DefaultTimezone', value: ltlDefaultTimezone }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
  dependsOn: [
    kvRoleAssignment
  ]
}

// ---------------------------------------------------------------------------
// Web container app — serves the SPA and reverse-proxies /api to the API app
// ---------------------------------------------------------------------------
resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: webAppName
  location: location
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 80
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'API_UPSTREAM', value: 'https://${apiApp.properties.configuration.ingress.fqdn}' }
            { name: 'RUNTIME_TENANT_ID', value: entraTenantId }
            { name: 'RUNTIME_WEB_CLIENT_ID', value: entraWebClientId }
            { name: 'RUNTIME_API_SCOPE', value: entraApiScope }
            { name: 'RUNTIME_API_BASE_URL', value: '/api' }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output webUrl string = 'https://${webApp.properties.configuration.ingress.fqdn}'
output keyVaultName string = keyVault.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
