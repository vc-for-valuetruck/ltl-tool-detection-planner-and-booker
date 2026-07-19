# one-shot-standalone.ps1
#
# One PowerShell script that provisions the LTL Standalone Azure stack AND
# deploys the API + Web images to it — with no local Docker required.
#
# Difference vs deploy-ltl-standalone.ps1:
#   - Also runs setup (setup-ltl-standalone.ps1's equivalent, inlined).
#   - Uses `az acr build` so no local Docker Desktop is needed.
#   - Randomizes SQL admin password if you don't provide one (and prints it).
#   - Prompts once for ALVYS_CLIENT_SECRET if it's not already in env.
#   - Verifies /api/health returns authMode=Demo before printing URLs.
#
# USAGE
#   .\scripts\one-shot-standalone.ps1
#
# What it prompts for:
#   1. Alvys client secret (only if $env:ALVYS_CLIENT_SECRET isn't set).
#
# What it prints at the end:
#   - Web URL, API URL, Swagger URL
#   - The randomized SQL admin password (save it!) — used again if you re-run.
#
# Cost: ~$35/mo (B1 App Service Plan + SQL S0 + ACR Basic + Log Analytics)
# Wall time: ~15 minutes from prompt to authMode=Demo.

[CmdletBinding()]
param(
    [string]$SubscriptionId  = '9dfdd151-fd80-4116-8c57-16f1d7156ded',
    [string]$ResourceGroup   = 'ltl-standalone-rg',
    [string]$BaseName        = 'ltl-standalone',
    [string]$Location        = 'centralus',
    [ValidateSet('Demo', 'EntraId')]
    [string]$AccessPolicyMode = 'Demo',
    [string]$AlvysClientSecret,
    [string]$SqlAdminPassword,
    [string]$AllowedEmailDomain = 'valuetruck.com'
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$m) { Write-Host "[one-shot] $m" -ForegroundColor Cyan }
function Write-Warn2([string]$m){ Write-Host "[one-shot] $m" -ForegroundColor Yellow }
function Write-Err2([string]$m) { Write-Host "[one-shot] $m" -ForegroundColor Red }

# --- 0. secrets: parameter > env > prompt ------------------------------------

if (-not $AlvysClientSecret -and $env:ALVYS_CLIENT_SECRET) { $AlvysClientSecret = $env:ALVYS_CLIENT_SECRET }
if (-not $AlvysClientSecret) {
    $secure = Read-Host "Paste the Alvys va336 client secret" -AsSecureString
    $AlvysClientSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if (-not $AlvysClientSecret) { Write-Err2 "Alvys client secret cannot be empty."; exit 1 }
}

if (-not $SqlAdminPassword -and $env:SQL_ADMIN_PASSWORD) { $SqlAdminPassword = $env:SQL_ADMIN_PASSWORD }
if (-not $SqlAdminPassword) {
    # Azure SQL: 8-128 chars, must contain 3 of upper/lower/digit/symbol.
    # Symbols kept to a safe subset that doesn't confuse connection strings.
    $upper  = -join ((65..90)  | Get-Random -Count 4 | ForEach-Object {[char]$_})
    $lower  = -join ((97..122) | Get-Random -Count 6 | ForEach-Object {[char]$_})
    $digit  = -join ((48..57)  | Get-Random -Count 4 | ForEach-Object {[char]$_})
    $symbol = -join ('!','@','#','%','*' | Get-Random -Count 2)
    $chars = ($upper + $lower + $digit + $symbol).ToCharArray()
    $SqlAdminPassword = -join ($chars | Sort-Object {Get-Random})
    Write-Warn2 "Generated SQL admin password (SAVE THIS): $SqlAdminPassword"
}

# --- 1. dependency + login checks --------------------------------------------

Write-Step "Checking Azure CLI..."
try { az --version | Out-Null } catch {
    Write-Err2 "Azure CLI not found. Install: winget install -e --id Microsoft.AzureCLI"
    exit 1
}

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
Write-Host "Signed in: $($account.user.name) — subscription $($account.name)" -ForegroundColor Green

# --- 2. provision infra via Bicep --------------------------------------------

Write-Step "Ensuring resource group $ResourceGroup exists in $Location..."
az group create --name $ResourceGroup --location $Location --output none

$repoRoot = Split-Path -Parent $PSScriptRoot
$bicep = Join-Path $repoRoot 'infra/uat/main.bicep'
if (-not (Test-Path $bicep)) { Write-Err2 "Bicep template not found at $bicep"; exit 1 }

Write-Step "Deploying Bicep (RG + ACR + SQL + App Service Plan + 2 App Services + Log Analytics + App Insights)..."
$deployName = "one-shot-$((Get-Date).ToString('yyyyMMddHHmmss'))"
az deployment group create `
    --resource-group $ResourceGroup `
    --name $deployName `
    --template-file $bicep `
    --parameters baseName=$BaseName sqlAdminPassword="$SqlAdminPassword" `
    --output none
if ($LASTEXITCODE -ne 0) { Write-Err2 "Bicep deployment failed. Check az deployment group show --name $deployName --resource-group $ResourceGroup"; exit 1 }

# --- 3. discover resource names ----------------------------------------------

Write-Step "Discovering resource names..."
$acrName    = az acr list         --resource-group $ResourceGroup --query "[0].name" -o tsv
$sqlServer  = az sql server list  --resource-group $ResourceGroup --query "[0].name" -o tsv
$apiApp     = "$BaseName-api"
$webApp     = "$BaseName-web"
$sqlDb      = "$BaseName-sqldb"
$acrLoginServer = az acr show --name $acrName --resource-group $ResourceGroup --query loginServer -o tsv

Write-Host "  ACR         = $acrName ($acrLoginServer)" -ForegroundColor DarkGray
Write-Host "  SQL server  = $sqlServer" -ForegroundColor DarkGray
Write-Host "  API app     = $apiApp"    -ForegroundColor DarkGray
Write-Host "  Web app     = $webApp"    -ForegroundColor DarkGray

# --- 4. build images inside Azure (no local Docker) --------------------------

$imageTag = (Get-Date -Format 'yyyyMMddHHmmss')

Write-Step "Building API image via az acr build (~4 min)..."
az acr build --registry $acrName `
    --image "ltl-api:$imageTag" `
    --file  (Join-Path $repoRoot 'src/LtlTool.Api/Dockerfile') `
    $repoRoot --output none
if ($LASTEXITCODE -ne 0) { Write-Err2 "API image build failed."; exit 1 }

Write-Step "Building Web image via az acr build (~4 min)..."
az acr build --registry $acrName `
    --image "ltl-web:$imageTag" `
    --file  (Join-Path $repoRoot 'web/Dockerfile') `
    (Join-Path $repoRoot 'web') --output none
if ($LASTEXITCODE -ne 0) { Write-Err2 "Web image build failed."; exit 1 }

# --- 5. wire App Services to fresh images ------------------------------------

$acrUser = az acr credential show --name $acrName --resource-group $ResourceGroup --query username -o tsv
$acrPass = az acr credential show --name $acrName --resource-group $ResourceGroup --query "passwords[0].value" -o tsv

Write-Step "Pointing $apiApp at ${acrLoginServer}/ltl-api:$imageTag..."
az webapp config container set --name $apiApp --resource-group $ResourceGroup `
    --docker-custom-image-name    "${acrLoginServer}/ltl-api:${imageTag}" `
    --docker-registry-server-url  "https://${acrLoginServer}" `
    --docker-registry-server-user  $acrUser `
    --docker-registry-server-password $acrPass --output none

Write-Step "Pointing $webApp at ${acrLoginServer}/ltl-web:$imageTag..."
az webapp config container set --name $webApp --resource-group $ResourceGroup `
    --docker-custom-image-name    "${acrLoginServer}/ltl-web:${imageTag}" `
    --docker-registry-server-url  "https://${acrLoginServer}" `
    --docker-registry-server-user  $acrUser `
    --docker-registry-server-password $acrPass --output none

# --- 6. app settings ---------------------------------------------------------

$sqlAdmin   = 'ltlsqladmin'
$sqlConnStr = "Server=tcp:${sqlServer}.database.windows.net,1433;Initial Catalog=${sqlDb};Persist Security Info=False;User ID=${sqlAdmin};Password=${SqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

$webFqdn = az webapp show --name $webApp --resource-group $ResourceGroup --query defaultHostName -o tsv
$apiFqdn = az webapp show --name $apiApp --resource-group $ResourceGroup --query defaultHostName -o tsv
$webUrl  = "https://$webFqdn"
$apiUrl  = "https://$apiFqdn"

Write-Step "Configuring $apiApp app settings (AccessPolicy:Mode=$AccessPolicyMode)..."
$apiSettings = @(
    'WEBSITES_PORT=8080',
    'ASPNETCORE_ENVIRONMENT=Production',
    "AccessPolicy__Mode=$AccessPolicyMode",
    "AccessPolicy__AllowedEmailDomains__0=$AllowedEmailDomain",
    # AzureAd placeholders satisfy MSAL's greedy option validation. In Demo
    # mode the AuthenticationSchemeRouter forwards to DemoAuthenticationHandler
    # so JwtBearer is never actually invoked; placeholders are safe.
    'AzureAd__TenantId=00000000-0000-0000-0000-000000000000',
    'AzureAd__ClientId=00000000-0000-0000-0000-000000000000',
    'AzureAd__ClientSecret=',
    'AzureAd__Instance=https://login.microsoftonline.com/',
    'AzureAd__Audience=api://00000000-0000-0000-0000-000000000000',
    "ConnectionStrings__DefaultConnection=$sqlConnStr",
    "Cors__AllowedOrigins__0=$webUrl",
    'Alvys__Provider=Live',
    'Alvys__ApiBaseUrl=https://integrations.alvys.com',
    'Alvys__ApiVersion=v1',
    'Alvys__TenantId=va336',
    'Alvys__ClientId=MZEDvQYVZmcEtk3f17QFgYTg19pa3eJL',
    "Alvys__ClientSecret=$AlvysClientSecret",
    'Alvys__Writeback__Mode=Disabled',
    'Ltl__DetectionEnabled=true',
    'Ltl__DefaultTimezone=America/Chicago'
)
az webapp config appsettings set --name $apiApp --resource-group $ResourceGroup --settings @apiSettings --output none

Write-Step "Configuring $webApp app settings..."
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
    Write-Err2 "EntraId mode not supported by this one-shot script yet. Use deploy-ltl-standalone.ps1 -AccessPolicyMode EntraId."
    exit 1
}
az webapp config appsettings set --name $webApp --resource-group $ResourceGroup --settings @webSettings --output none

# --- 7. restart + smoke test -------------------------------------------------

Write-Step "Restarting App Services (image pull takes ~60s)..."
az webapp restart --name $apiApp --resource-group $ResourceGroup --output none
az webapp restart --name $webApp --resource-group $ResourceGroup --output none

Write-Step "Waiting up to 180s for API to report authMode=$AccessPolicyMode..."
$deadline = (Get-Date).AddSeconds(180)
$authMode = $null
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "$apiUrl/api/health" -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) {
            $body = $r.Content | ConvertFrom-Json
            if ($body.authMode) { $authMode = $body.authMode; break }
        }
    } catch { }
    Start-Sleep -Seconds 5
}

if ($authMode -eq $AccessPolicyMode) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "  Deploy complete. authMode=$authMode confirmed." -ForegroundColor Green
    Write-Host ""
    Write-Host "  Web UI:    $webUrl"                     -ForegroundColor White
    Write-Host "  API:       $apiUrl/api"                 -ForegroundColor White
    Write-Host "  Swagger:   $apiUrl/swagger"             -ForegroundColor White
    Write-Host "  Health:    $apiUrl/api/health"          -ForegroundColor White
    Write-Host ""
    Write-Host "  SQL admin password: $SqlAdminPassword"  -ForegroundColor Yellow
    Write-Host "    (needed to re-run this script or connect to $sqlServer)" -ForegroundColor DarkGray
    if ($AccessPolicyMode -eq 'Demo') {
        Write-Host ""
        Write-Host "  DEMO MODE: every request is authenticated as demo@valuetruck.com." -ForegroundColor Yellow
        Write-Host "  Do not share this URL externally until AccessPolicy:Mode=EntraId."  -ForegroundColor Yellow
    }
    Write-Host "============================================================" -ForegroundColor Green
} else {
    Write-Err2 "API did not report authMode=$AccessPolicyMode within 180s."
    Write-Err2 "Check logs: az webapp log tail --name $apiApp --resource-group $ResourceGroup"
    Write-Err2 "URLs (may still boot in a few more minutes):"
    Write-Host "  Web UI:  $webUrl"
    Write-Host "  API:     $apiUrl/api"
    exit 1
}
