# Publish a self-contained Native AOT build. Default RID is win-x64.
# Release builds pass DebugType=none so the output has no .pdb or .dbg.
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishArgs = @(
    (Join-Path $root "src/AirPlayServer.Host/AirPlayServer.Host.csproj"),
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true"
)
if ($Configuration -eq "Release") {
    $publishArgs += @(
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:NativeDebugSymbols=false"
    )
}
$output = Join-Path $root "artifacts/$Runtime"
dotnet publish @publishArgs --output $output
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
if ($Configuration -eq "Release") {
    $symbols = Get-ChildItem -Path $output -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '^\.(pdb|dbg|dwo|debug|sym|dsym)$' }
    if ($symbols) {
        $symbols | ForEach-Object { Write-Error "publish directory contains debug symbols: $($_.FullName)" }
        exit 1
    }
}
Write-Host "Published $Runtime to $output"
