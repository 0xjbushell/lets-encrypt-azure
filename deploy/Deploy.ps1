<#
.SYNOPSIS

Helper script to deploy the infrastructure to Azure.

.PARAMETER ResourceGroupName

Name of the resourcegroup to deploy. Name of resources will be derived from it.

#>

param(
    [Parameter(Mandatory=$true)]
    [string] $ResourceGroupName,
    [string] $ResourceGroupLocation = "westeurope",
    [ValidateSet("Complete", "Incremental")]
    [string] $Mode = "Complete"
)
$ErrorActionPreference = "Stop"

if (!$ResourceGroupName) {
    throw "Resourcegroup name must be set"
}

$ResourceGroup = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
if ($ResourceGroup -eq $null) {
    Write-Output "Creating resourcegroup $ResourceGroupName in $ResourceGroupLocation"
    New-AzResourceGroup -Name $ResourceGroupName -Location $ResourceGroupLocation -Force -ErrorAction Stop
}

Write-Output "Deploying to resourcegroup $ResourceGroupName"

$templateFile = Join-Path $PSScriptRoot "deploy.json"
$parameters = @{}

New-AzResourceGroupDeployment `
    -ResourceGroupName $ResourceGroupName `
    -TemplateFile $templateFile `
    -TemplateParameterObject $parameters `
    -Mode $Mode `
    -Force
