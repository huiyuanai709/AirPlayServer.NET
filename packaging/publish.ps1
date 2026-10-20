# Publish a self-contained Native AOT build. Default RID is win-x64.
# Release builds pass DebugType=none. Third-party native .pdb/.dbg files
# (HarfBuzz, Skia, Avalonia) are removed before the symbol check.
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
    $symbolPattern = '(?i)\.(pdb|dbg|dwo|debug|sym|dsym)$'
    $symbols = @(Get-ChildItem -Path $output -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $symbolPattern })
    foreach ($symbol in $symbols) {
        Write-Host "removing debug symbol: $($symbol.FullName)"
        Remove-Item -LiteralPath $symbol.FullName -Force
    }
    $left = @(Get-ChildItem -Path $output -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $symbolPattern })
    if ($left.Count -gt 0) {
        $left | ForEach-Object { Write-Error "publish directory contains debug symbols: $($_.FullName)" }
        exit 1
    }
}
Write-Host "Published $Runtime to $output"
