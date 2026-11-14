<#
.SYNOPSIS
  Deletes everything the PoC created (the whole resource group).
#>
param(
    [string] $Subscription = 'UpendraPractice',
    [string] $ResourceGroup = 'rg-family-gallery'
)

az account set --subscription $Subscription
az group delete -n $ResourceGroup --yes
