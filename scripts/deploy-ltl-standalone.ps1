# deploy-ltl-standalone.ps1
#
# Second-phase deploy for the LTL standalone stack. Assumes
# `setup-ltl-standalone.ps1` has already provisioned the infra (resource group,
# App Service Plan, ACR, SQL server + DB, Log Analytics, App Insights, and the
# two placeholder App Services).
#
# What this script does:
#   1. Builds the API and Web container images locally with Docker.
#   2. Pushes both to the standalone ACR (ltlstandaloneacr).
#   3. Points the two App Services (ltl-standalone-api, ltl-standalone-web) at
#      the freshly-pushed images.
#   4. Configures application settings for demo mode by default: the whole
#      stack becomes reachable via a public URL under demo@valuetruck.com
#      synthetic identity, live-reading va336 Alvys. No Entra tenant needed
#      to prove leadership the pilot works.
#
# WHY DEMO MODE BY DEFAULT
#   The Contributor role assignment for real Entra is still pending. Rather
#   than let that block the pilot demo, we ship with AccessPolicy:Mode=Demo,
#   which uses the DemoAuthenticationHandler shipped in PR #61. The API's
#   /api/health endpoint publishes authMode=Demo so anyone can verify.
#
#   Four independent guardrails prevent accidental Demo shipping to a
#   public-internet-scale deployment:
#     1. Default in code is EntraId; Demo requires explicit opt-in.
#     2. Startup logs a multi-line warning banner.
#     3. /api/health returns authMode ("Demo" | "EntraId").
#     4. Handler is a separate .cs file, only wired when mode matches.
#
#   Flip to EntraId later by re-running this script with
#   -AccessPolicyMode EntraId plus the four AZURE_AD_* env vars set.
#
# PREREQUISITES
#   - `setup-ltl-standalone.ps1` completed (RG + apps exist).
#   - Docker Desktop running locally.
#   - Alvys va336 client secret in the ALVYS_CLIENT_SECRET env var.
#   - Same SQL admin password you set in setup-ltl-standalone.ps1, in
#     SQL_ADMIN_PASSWORD env var.
#
# USAGE
#   $env:ALVYS_CLIENT_SECRET = "<your va336 secret>"
#   $env:SQL_ADMIN_PASSWORD  = "<the SQL password from setup>"
#   .\scripts\deploy-ltl-standalone.ps1
#
#   Optional switches:
#     -AccessPolicyMode <Demo|EntraId>  Default: Demo
#     -ResourceGroup    <name>          Default: ltl-standalone-rg
#     -BaseName         <name>          Default: ltl-standalone
#     -ImageTag         <tag>           Default: yyyymmddHHMMSS
#     -SubscriptionId   <guid>          Default: 9dfdd151-fd80-4116-8c57-16f1d7156ded

[CmdletBinding()]
param(
    [ValidateSet('Demo', 'EntraId')]
    [string]$AccessPolicyMode = 'Demo',

    [string]$ResourceGroup  = 'ltl-standalone-rg',
    [string]$BaseName       = 'ltl-standalone',
    [string]$SubscriptionId = '9dfdd151-fd80-4116-8c57-16f1d7156ded',
    [string]$ImageTag       = (Get-Date -Format 'yyyyMMddHHmmss'),

    # Alvys — read-only against va336. Client id is public; secret must come
    # from env or -AlvysClientSecret. Never commit either.
    [string]$AlvysTenantId       = 'va336',
    [string]$AlvysClientId       = 'MZEDvQYVZmcEtk3f17QFgYTg19pa3eJL',
    [string]$AlvysApiBaseUrl     = 'https://integrations.alvys.com',
    [string]$AlvysApiVersion     = 'v1',
    [ValidateSet('Live', 'Fallback')]
    [string]$AlvysProvider       = 'Live',
    [ValidateSet('Disabled', 'Enabled')]
    [string]$AlvysWritebackMode  = 'Disabled',

    # Only used when AccessPolicyMode = EntraId. Leave defaults for Demo mode.
    [string]$AzureAdTenantId     = '00000000-0000-0000-0000-000000000000',
    [string]$AzureAdApiClientId  = '00000000-0000-0000-0000-000000000000',
    [string]$AzureAdWebClientId  = '00000000-0000-0000-0000-000000000000',
    [string]$AzureAdClientSecret = '',
    [string]$AllowedEmailDomain  = 'valuetruck.com',

    [string]$AlvysClientSecret,
    [string]$SqlAdminPassword
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg)  { Write-Host "[deploy] $msg" -ForegroundColor Cyan }
function Write-Warn2([string]$msg) { Write-Host "[deploy] $msg" -ForegroundColor Yellow }
function Write-Err2([string]$msg)  { Write-Host "[deploy] $msg" -ForegroundColor Red }

# --- 0. resolve secrets (parameter > env var) --------------------------------

if (-not $AlvysClientSecret -and $env:ALVYS_CLIENT_SECRET) { $AlvysClientSecret = $env:ALVYS_CLIENT_SECRET }
if (-not $SqlAdminPassword  -and $env:SQL_ADMIN_PASSWORD)  { $SqlAdminPassword  = $env:SQL_ADMIN_PASSWORD  }

if (-not $AlvysClientSecret) {
    Write-Err2 "ALVYS_CLIENT_SECRET is required (via -AlvysClientSecret or env)."
    exit 1
}
if (-not $SqlAdminPassword) {
    Write-Err2 "SQL_ADMIN_PASSWORD is required (via -SqlAdminPassword or env; same value used in setup-ltl-standalone.ps1)."
    exit 1
}

if ($AccessPolicyMode -eq 'EntraId') {
    if (-not $AzureAdClientSecret -and $env:AZURE_AD_CLIENT_SECRET) { $AzureAdClientSecret = $env:AZURE_AD_CLIENT_SECRET }
    if ($AzureAdTenantId    -eq '00000000-0000-0000-0000-000000000000' -or
        $AzureAdApiClientId -eq '00000000-0000-0000-0000-000000000000' -or
        -not $AzureAdClientSecret) {
        Write-Err2 "EntraId mode selected but AzureAd values are still placeholders. Provide -AzureAdTenantId, -AzureAdApiClientId, -AzureAdWebClientId, -AzureAdClientSecret (or env vars)."
        exit 1
    }
}

# --- 1. dependency check -----------------------------------------------------

Write-Step "Checking prerequisites..."
try {
    docker info | Out-Null
} catch {
    Write-Err2 "Docker Desktop is not running. Start Docker and re-run."
    exit 1
}
$azOk = $false
try { az --version | Out-Null; $azOk = $true } catch { }
if (-not $azOk) {
    Write-Err2 "Azure CLI not found. winget install -e --id Microsoft.AzureCLI, then reopen PowerShell."
    exit 1
}

# --- 2. login + subscription -------------------------------------------------

Write-Step "Verifying Azure login..."
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Warn2 "Not logged in. Opening browser..."
    az login | Out-Null
    $account = az account show --output json | ConvertFrom-Json
}
if ($account.id -ne $SubscriptionId) {
    az account set --subscription $SubscriptionId | Out-Null
    $account = az account show --output json | ConvertFrom-Json
}
Write-Host "Signed in as $($account.user.name); subscription $($account.name)." -ForegroundColor Green

# --- 3. discover ACR + SQL + apps in the standalone RG -----------------------

Write-Step "Discovering resources in $ResourceGroup..."
$acrName    = az acr list         --resource-group $ResourceGroup --query "[0].name"                          -o tsv
$sqlServer  = az sql server list  --resource-group $ResourceGroup --query "[0].name"                          -o tsv
$apiApp     = "$BaseName-api"
$webApp     = "$BaseName-web"
$sqlDb      = "$BaseName-sqldb"
if (-not $acrName)   { Write-Err2 "No ACR found in $ResourceGroup. Run setup-ltl-standalone.ps1 first."; exit 1 }
if (-not $sqlServer) { Write-Err2 "No SQL server found in $ResourceGroup. Run setup-ltl-standalone.ps1 first."; exit 1 }

$acrLoginServer = az acr show --name $acrName --resource-group $ResourceGroup --query loginServer -o tsv
Write-Host "  ACR         = $acrName ($acrLoginServer)" -ForegroundColor DarkGray
Write-Host "  SQL server  = $sqlServer" -ForegroundColor DarkGray
Write-Host "  API app     = $apiApp"    -ForegroundColor DarkGray
Write-Host "  Web app     = $webApp"    -ForegroundColor DarkGray

# --- 4. build + push images --------------------------------------------------

Write-Step "Logging into ACR..."
az acr login --name $acrName | Out-Null

$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Step "Building API image ${acrLoginServer}/ltl-api:${ImageTag}..."
docker build -f (Join-Path $repoRoot 'src/LtlTool.Api/Dockerfile') -t "${acrLoginServer}/ltl-api:${ImageTag}" $repoRoot
if ($LASTEXITCODE -ne 0) { Write-Err2 "API image build failed."; exit 1 }

Write-Step "Building Web image ${acrLoginServer}/ltl-web:${ImageTag}..."
docker build -f (Join-Path $repoRoot 'web/Dockerfile') -t "${acrLoginServer}/ltl-web:${ImageTag}" (Join-Path $repoRoot 'web')
if ($LASTEXITCODE -ne 0) { Write-Err2 "Web image build failed."; exit 1 }

Write-Step "Pushing images to $acrLoginServer..."
docker push "${acrLoginServer}/ltl-api:${ImageTag}"
docker push "${acrLoginServer}/ltl-web:${ImageTag}"

# --- 5. wire App Services to the new images ---------------------------------

$acrUser = az acr credential show --name $acrName --resource-group $ResourceGroup --query username         -o tsv
$acrPass = az acr credential show --name $acrName --resource-group $ResourceGroup --query "passwords[0].value" -o tsv

Write-Step "Pointing $apiApp at ${acrLoginServer}/ltl-api:${ImageTag}..."
az webapp config container set --name $apiApp --resource-group $ResourceGroup `
    --docker-custom-image-name    "${acrLoginServer}/ltl-api:${ImageTag}" `
    --docker-registry-server-url  "https://${acrLoginServer}" `
    --docker-registry-server-user  $acrUser `
    --docker-registry-server-password $acrPass | Out-Null

Write-Step "Pointing $webApp at ${acrLoginServer}/ltl-web:${ImageTag}..."
az webapp config container set --name $webApp --resource-group $ResourceGroup `
    --docker-custom-image-name    "${acrLoginServer}/ltl-web:${ImageTag}" `
    --docker-registry-server-url  "https://${acrLoginServer}" `
    --docker-registry-server-user  $acrUser `
    --docker-registry-server-password $acrPass | Out-Null

# --- 6. app settings --------------------------------------------------------

$sqlAdmin   = 'ltlsqladmin'
$sqlConnStr = "Server=tcp:${sqlServer}.database.windows.net,1433;Initial Catalog=${sqlDb};Persist Security Info=False;User ID=${sqlAdmin};Password=${SqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

$webFqdn = az webapp show --name $webApp --resource-group $ResourceGroup --query defaultHostName -o tsv
$apiFqdn = az webapp show --name $apiApp --resource-group $ResourceGroup --query defaultHostName -o tsv
$webUrl  = "https://${webFqdn}"
$apiUrl  = "https://${apiFqdn}"

Write-Step "Configuring $apiApp application settings (AccessPolicy:Mode=$AccessPolicyMode)..."
$apiSettings = @(
    'WEBSITES_PORT=8080',
    'ASPNETCORE_ENVIRONMENT=Production',
    "AccessPolicy__Mode=$AccessPolicyMode",
    "AccessPolicy__AllowedEmailDomains__0=$AllowedEmailDomain",
    # AzureAd placeholders satisfy MSAL's greedy option validation. In Demo
    # mode the AuthenticationSchemeRouter forwards to DemoAuthenticationHandler
    # so JwtBearer is never actually invoked; placeholders are safe.
    "AzureAd__TenantId=$AzureAdTenantId",
    "AzureAd__ClientId=$AzureAdApiClientId",
    "AzureAd__ClientSecret=$AzureAdClientSecret",
    'AzureAd__Instance=https://login.microsoftonline.com/',
    "AzureAd__Audience=api://$AzureAdApiClientId",
    "ConnectionStrings__DefaultConnection=$sqlConnStr",
    "Cors__AllowedOrigins__0=$webUrl",
    "Alvys__Provider=$AlvysProvider",
    "Alvys__ApiBaseUrl=$AlvysApiBaseUrl",
    "Alvys__ApiVersion=$AlvysApiVersion",
    "Alvys__TenantId=$AlvysTenantId",
    "Alvys__ClientId=$AlvysClientId",
    "Alvys__ClientSecret=$AlvysClientSecret",
    "Alvys__Writeback__Mode=$AlvysWritebackMode",
    'Ltl__DetectionEnabled=true',
    'Ltl__DefaultTimezone=America/Chicago'
)
az webapp config appsettings set --name $apiApp --resource-group $ResourceGroup --settings @apiSettings | Out-Null

Write-Step "Configuring $webApp application settings..."
# In Demo mode the RUNTIME_* Entra values stay blank so the Angular MSAL guard
# suppresses the auth redirect. The API's demo handler admits all requests.
if ($AccessPolicyMode -eq 'Demo') {
    $webSettings = @(
        'WEBSITES_PORT=80',
        "API_UPSTREAM=$apiUrl",
        'RUNTIME_TENANT_ID=',
        'RUNTIME_WEB_CLIENT_ID=',
        'RUNTIME_API_SCOPE=',
        'RUNTIME_API_BASE_URL=/api'
    )
} else {
    $webSettings = @(
        'WEBSITES_PORT=80',
        "API_UPSTREAM=$apiUrl",
        "RUNTIME_TENANT_ID=$AzureAdTenantId",
        "RUNTIME_WEB_CLIENT_ID=$AzureAdWebClientId",
        "RUNTIME_API_SCOPE=api://$AzureAdApiClientId/access_as_user",
        'RUNTIME_API_BASE_URL=/api'
    )
}
az webapp config appsettings set --name $webApp --resource-group $ResourceGroup --settings @webSettings | Out-Null

# --- 7. restart + summary ----------------------------------------------------

Write-Step "Restarting App Services..."
az webapp restart --name $apiApp --resource-group $ResourceGroup | Out-Null
az webapp restart --name $webApp --resource-group $ResourceGroup | Out-Null

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Deployed. Give App Service ~90s to pull the images."         -ForegroundColor Cyan
Write-Host ""
Write-Host "  Web:    $webUrl"                                             -ForegroundColor White
Write-Host "  API:    $apiUrl"                                             -ForegroundColor White
Write-Host "  Health: $apiUrl/api/health  (expect authMode=$AccessPolicyMode)" -ForegroundColor White
Write-Host ""
if ($AccessPolicyMode -eq 'Demo') {
    Write-Host "  DEMO MODE: every request is authenticated as demo@valuetruck.com" -ForegroundColor Yellow
    Write-Host "  Never expose this URL externally. Flip to EntraId when the"      -ForegroundColor Yellow
    Write-Host "  Entra Contributor role assignment is in place:"                  -ForegroundColor Yellow
    Write-Host "    .\scripts\deploy-ltl-standalone.ps1 -AccessPolicyMode EntraId" -ForegroundColor Yellow
    Write-Host "        -AzureAdTenantId <guid> -AzureAdApiClientId <guid>"        -ForegroundColor Yellow
    Write-Host "        -AzureAdWebClientId <guid>"                                -ForegroundColor Yellow
    Write-Host "        (AZURE_AD_CLIENT_SECRET env var)"                          -ForegroundColor Yellow
}
Write-Host "============================================================" -ForegroundColor Cyan
