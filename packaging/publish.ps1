# Publish a self-contained Native AOT build. Default RID is win-x64.
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $root "src/AirPlayServer.Host/AirPlayServer.Host.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output (Join-Path $root "artifacts/$Runtime")
Write-Host "Published $Runtime to $root/artifacts/$Runtime"
