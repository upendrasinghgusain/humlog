<#
.SYNOPSIS
  Creates (or updates) everything for the Humlog PoC and deploys the code. Safe to re-run.

.DESCRIPTION
  The website (index.html, app.js, catalogue.json) lives in its own storage account whose firewall only admits
  Cloudflare (which enforces the Access login on humlog.upendragusain.co.uk) and the sync function's subnet.
  The function's own storage account holds its code package and sync state.

.EXAMPLE
  ./deploy.ps1 -ShareUrl https://photos.app.goo.gl/xxxx    # first time
  ./deploy.ps1                                             # later: redeploy code, keep the existing share link
#>
param(
    [string] $ShareUrl,
    [string] $Subscription = 'UpendraPractice',
    [string] $ResourceGroup = 'rg-family-gallery',
    [string] $Location = 'centralindia',
    [string] $CustomDomain = 'humlog.upendragusain.co.uk'
)

# Failures are detected through $LASTEXITCODE (Invoke-Az); 'Stop' would also trip on az's harmless stderr output.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Az {
    az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0..2] -join ' ') failed" }
}

Invoke-Az account set --subscription $Subscription
$suffix = (az account show --query id -o tsv).Substring(0, 6)
$funcStorage = "stfamgallery$suffix"
$webStorage = "sthumlog$suffix"
$funcApp = "func-family-gallery-$suffix"
$vnet = 'vnet-family-gallery'
$subnet = 'snet-func'

Write-Host "Resource group $ResourceGroup ($Location)"
Invoke-Az group create -n $ResourceGroup -l $Location -o none

Write-Host "Network $vnet/$subnet (function -> website storage)"
if (-not (az network vnet show -g $ResourceGroup -n $vnet --query name -o tsv 2>$null)) {
    Invoke-Az network vnet create -g $ResourceGroup -n $vnet -l $Location --address-prefixes 10.20.0.0/24 `
        --subnet-name $subnet --subnet-prefixes 10.20.0.0/26 -o none
}
Invoke-Az network vnet subnet update -g $ResourceGroup --vnet-name $vnet -n $subnet `
    --delegations Microsoft.App/environments --service-endpoints Microsoft.Storage -o none
$subnetId = az network vnet subnet show -g $ResourceGroup --vnet-name $vnet -n $subnet --query id -o tsv

foreach ($account in $funcStorage, $webStorage) {
    if (-not (az storage account show -n $account -g $ResourceGroup --query name -o tsv 2>$null)) {
        Write-Host "Storage account $account"
        Invoke-Az storage account create -n $account -g $ResourceGroup -l $Location --sku Standard_LRS --kind StorageV2 `
            --min-tls-version TLS1_2 --allow-blob-public-access false -o none
    }
}

Write-Host "Website storage $webStorage (static website, Cloudflare + function subnet only)"
$webConn = az storage account show-connection-string -n $webStorage -g $ResourceGroup --query connectionString -o tsv
$existing = @(az storage account network-rule list -n $webStorage -g $ResourceGroup --query "ipRules[].ipAddressOrRange" -o tsv)
foreach ($range in (Invoke-RestMethod https://www.cloudflare.com/ips-v4) -split "`n" | Where-Object { $_ }) {
    if ($existing -notcontains $range) {
        Invoke-Az storage account network-rule add -n $webStorage -g $ResourceGroup --ip-address $range -o none
    }
}
Invoke-Az storage account network-rule add -n $webStorage -g $ResourceGroup --subnet $subnetId -o none
# Only when needed: any account update re-verifies the custom domain, which fails once Cloudflare proxies the CNAME.
if ((az storage account show -n $webStorage -g $ResourceGroup --query networkRuleSet.defaultAction -o tsv) -ne 'Deny') {
    Invoke-Az storage account update -n $webStorage -g $ResourceGroup --default-action Deny -o none
}

Write-Host "Function app $funcApp (Flex Consumption, .NET 10)"
if (-not (az functionapp show -n $funcApp -g $ResourceGroup --query name -o tsv 2>$null)) {
    if (-not $ShareUrl) { throw 'First deployment: pass -ShareUrl.' }
    Invoke-Az functionapp create -n $funcApp -g $ResourceGroup --storage-account $funcStorage `
        --flexconsumption-location $Location --runtime dotnet-isolated --runtime-version 10 --instance-memory 512 -o none
}
if (-not (az functionapp show -n $funcApp -g $ResourceGroup --query virtualNetworkSubnetId -o tsv 2>$null)) {
    Invoke-Az functionapp vnet-integration add -n $funcApp -g $ResourceGroup --vnet $vnet --subnet $subnet -o none
}
$settings = @("WEB_STORAGE=$webConn")
# The share link is a key to the album: it lives only in this app setting, never in git.
if ($ShareUrl) { $settings += "ALBUM_SHARE_URL=$ShareUrl" }
Invoke-Az functionapp config appsettings set -n $funcApp -g $ResourceGroup --settings @settings -o none

# The web page is packaged with the function, which uploads it into the firewalled website account when it starts.
Write-Host 'Publishing sync function + web page'
$out = Join-Path ([IO.Path]::GetTempPath()) "humlog-sync"
$zip = "$out.zip"
Remove-Item -Recurse -Force $out, $zip -ErrorAction SilentlyContinue
dotnet publish "$root/sync/Humlog.Sync.csproj" -c Release -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
tar -a -c -f $zip -C $out *
Invoke-Az functionapp deployment source config-zip -n $funcApp -g $ResourceGroup --src $zip -o none

$origin = (az storage account show -n $webStorage -g $ResourceGroup --query primaryEndpoints.web -o tsv) -replace '^https://|/$', ''
Write-Host "`nWebsite origin (Cloudflare CNAME target): $origin"
Write-Host "Public address (after Cloudflare setup):  https://$CustomDomain/"
