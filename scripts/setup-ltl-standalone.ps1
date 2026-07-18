# Setup script: LTL Standalone module infrastructure on Azure
#
# Provisions a fully separate LTL app deployment into a new resource group
# (ltl-standalone-rg), isolated from ltl-uat-rg. Reuses the same Bicep template
# under infra/uat/ but with different names so nothing collides.
#
# What this creates in your Azure subscription (9dfdd151-fd80-4116-8c57-16f1d7156ded):
#   Resource group:    ltl-standalone-rg (in centralus)
#   App Service Plan:  ltl-standalone-plan
#   Web app:           ltl-standalone-web
#   API app:           ltl-standalone-api
#   Azure Container Registry: ltlstandaloneacr
#   Azure SQL server:  ltl-standalone-sql-<unique>
#   Azure SQL DB:      ltl-standalone-sqldb (S0)
#   Log Analytics WS:  ltl-standalone-law
#   App Insights:      ltl-standalone-ai
#
# Prerequisites:
#   1. Azure CLI installed: winget install -e --id Microsoft.AzureCLI
#      (verify: `az --version`)
#   2. You are logged in as a user who has Contributor (or Owner) on the
#      target subscription. If not, this script will prompt `az login`.
#   3. You will provide a SQL admin password interactively. It must meet
#      Azure SQL complexity requirements: 8-128 chars, three of upper/
#      lower/digit/symbol, not containing 'ltlsqladmin'.
#
# Usage:
#   From the LTL repo root in PowerShell:
#     .\scripts\setup-ltl-standalone.ps1
#
#   Optional switches:
#     -Location <region>       Default: centralus
#     -BaseName <name>         Default: ltl-standalone
#     -ResourceGroup <name>    Default: ltl-standalone-rg
#     -SubscriptionId <guid>   Default: 9dfdd151-fd80-4116-8c57-16f1d7156ded
#     -WhatIf                  Show what would be deployed, do not deploy
#
# Idempotency: safe to re-run. Existing resources are updated in place; new
# ones are created. The Bicep template drives idempotency at the resource level.

[CmdletBinding()]
param(
    [string]$Location       = 'centralus',
    [string]$BaseName       = 'ltl-standalone',
    [string]$ResourceGroup  = 'ltl-standalone-rg',
    [string]$SubscriptionId = '9dfdd151-fd80-4116-8c57-16f1d7156ded',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# -- 0. locate the Bicep template ---------------------------------------------

$repoRoot     = Split-Path -Parent $PSScriptRoot
$bicepPath    = Join-Path $repoRoot 'infra/uat/main.bicep'
if (-not (Test-Path $bicepPath)) {
    Write-Error "Bicep template not found at $bicepPath. Run this from the LTL repo (scripts/ is a sibling of infra/)."
}
Write-Host "Using Bicep template: $bicepPath" -ForegroundColor Cyan

# -- 1. verify az CLI is available --------------------------------------------

try {
    $azVersion = (az --version 2>$null | Select-String -Pattern 'azure-cli\s+(\S+)' | Select-Object -First 1)
    if (-not $azVersion) { throw 'az not found' }
    Write-Host "Azure CLI: $azVersion" -ForegroundColor Cyan
}
catch {
    Write-Error @"
Azure CLI not found. Install it first:
    winget install -e --id Microsoft.AzureCLI
Then reopen PowerShell and re-run this script.
"@
}

# -- 2. login + subscription --------------------------------------------------

Write-Host "`nChecking Azure login..." -ForegroundColor Cyan
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in. Opening browser for az login..." -ForegroundColor Yellow
    az login | Out-Null
    $account = az account show --output json | ConvertFrom-Json
}
Write-Host "Signed in as: $($account.user.name)" -ForegroundColor Green

if ($account.id -ne $SubscriptionId) {
    Write-Host "Switching to subscription $SubscriptionId..." -ForegroundColor Cyan
    az account set --subscription $SubscriptionId
    $account = az account show --output json | ConvertFrom-Json
}
Write-Host "Active subscription: $($account.name) ($($account.id))" -ForegroundColor Green

# -- 3. prompt for SQL admin password (secure) --------------------------------

Write-Host "`nSQL admin password" -ForegroundColor Cyan
Write-Host "Requirements: 8-128 chars; must contain three of upper / lower / digit / symbol." -ForegroundColor DarkGray
$sqlPasswordSecure = Read-Host -AsSecureString "Enter SQL admin password (input hidden)"
$sqlPasswordSecure2 = Read-Host -AsSecureString "Confirm SQL admin password"

$bstr1 = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sqlPasswordSecure)
$bstr2 = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sqlPasswordSecure2)
try {
    $plain1 = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr1)
    $plain2 = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr2)
    if ($plain1 -ne $plain2) {
        Write-Error 'Passwords do not match. Aborting without deploying.'
    }
    if ($plain1.Length -lt 8) {
        Write-Error 'Password too short. Azure SQL requires at least 8 characters.'
    }
    if ($plain1 -match 'ltlsqladmin') {
        Write-Error "Password must not contain the SQL admin login name 'ltlsqladmin'."
    }
    $sqlPassword = $plain1
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr1)
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2)
}

# -- 4. create resource group -------------------------------------------------

Write-Host "`nEnsuring resource group $ResourceGroup exists in $Location..." -ForegroundColor Cyan
$rg = az group show --name $ResourceGroup --output json 2>$null | ConvertFrom-Json
if (-not $rg) {
    az group create --name $ResourceGroup --location $Location --tags "project=ltl-standalone" "createdBy=setup-ltl-standalone.ps1" | Out-Null
    Write-Host "Created resource group $ResourceGroup." -ForegroundColor Green
} else {
    Write-Host "Resource group already exists at $($rg.location)." -ForegroundColor DarkGray
}

# -- 5. what-if or deploy Bicep -----------------------------------------------

$deploymentName = "ltl-standalone-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$commonArgs = @(
    '--resource-group', $ResourceGroup
    '--name',           $deploymentName
    '--template-file',  $bicepPath
    '--parameters',     "baseName=$BaseName"
    '--parameters',     "sqlAdminPassword=$sqlPassword"
    '--parameters',     "location=$Location"
)

if ($WhatIf) {
    Write-Host "`n[WhatIf] Preview of resources this deployment would create/modify:" -ForegroundColor Yellow
    az deployment group what-if @commonArgs
    Write-Host "`n[WhatIf] Nothing was deployed. Re-run without -WhatIf to apply." -ForegroundColor Yellow
    return
}

Write-Host "`nDeploying Bicep template ($deploymentName). This takes 4-8 minutes..." -ForegroundColor Cyan
$deployJson = az deployment group create @commonArgs --output json
if ($LASTEXITCODE -ne 0) {
    Write-Error "Deployment failed. Check the Azure Portal Activity Log under $ResourceGroup for the exact error."
}
$deploy = $deployJson | ConvertFrom-Json
if ($deploy.properties.provisioningState -ne 'Succeeded') {
    Write-Error "Deployment finished with state $($deploy.properties.provisioningState)."
}
Write-Host "Deployment succeeded." -ForegroundColor Green

# -- 6. summary ---------------------------------------------------------------

Write-Host "`n== Provisioned resources ==" -ForegroundColor Cyan
az resource list --resource-group $ResourceGroup --output table --query "sort_by([].{Name:name, Type:type, Location:location}, &Type)"

Write-Host "`n== Next steps ==" -ForegroundColor Cyan
Write-Host "  1. Grant the ltl-uat-github-deploy service principal Contributor on $ResourceGroup" -ForegroundColor White
Write-Host "     (same Path B click-path as docs/AZURE_UAT_DEPLOY.md, but target this RG)." -ForegroundColor DarkGray
Write-Host "  2. Set GitHub environment secrets under a new 'standalone' environment:" -ForegroundColor White
Write-Host "       AZURE_CLIENT_ID     = eda7036b-7596-4c2a-836c-599fcd3a5166" -ForegroundColor DarkGray
Write-Host "       AZURE_TENANT_ID     = 99d7bd71-9046-4915-be1c-3aae2baf1645" -ForegroundColor DarkGray
Write-Host "       AZURE_SUBSCRIPTION_ID = $SubscriptionId" -ForegroundColor DarkGray
Write-Host "       SQL_ADMIN_PASSWORD  = <the password you just entered>" -ForegroundColor DarkGray
Write-Host "  3. Copy .github/workflows/deploy-ltl-uat.yml -> deploy-ltl-standalone.yml," -ForegroundColor White
Write-Host "     swap the environment name + resource group, and dispatch the workflow." -ForegroundColor DarkGray
Write-Host "  4. If you need a public URL, ensure the App Service is not IP-restricted" -ForegroundColor White
Write-Host "     (default is public + AAD auth via the LTL app's Entra registration)." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Done." -ForegroundColor Green
