# Publish a self-contained Native AOT build. Default RID is win-x64.
# Flavor lean (default) does not reference Avalonia. Flavor ui ships the
# mirror window. Release builds pass DebugType=none. Third-party native
# .pdb/.dbg files are removed, then Skia/HarfBuzz symbol tables are stripped.
# Optional env: PUBLISH_VERSION, PUBLISH_INFORMATIONAL_VERSION (release.yml).
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [ValidateSet("lean", "ui")]
    [string]$Flavor = "lean"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$enableAvalonia = if ($Flavor -eq "ui") { "true" } else { "false" }
$suffix = if ($Flavor -eq "ui") { "-ui" } else { "" }
$publishArgs = @(
    (Join-Path $root "src/AirPlayServer.Host/AirPlayServer.Host.csproj"),
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true",
    "-p:EnableAvalonia=$enableAvalonia"
)
if ($env:PUBLISH_VERSION) {
    $publishArgs += "-p:Version=$($env:PUBLISH_VERSION)"
}
if ($env:PUBLISH_INFORMATIONAL_VERSION) {
    $publishArgs += "-p:InformationalVersion=$($env:PUBLISH_INFORMATIONAL_VERSION)"
    $publishArgs += "-p:IncludeSourceRevisionInInformationalVersion=false"
}
if ($Configuration -eq "Release") {
    $publishArgs += @(
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:NativeDebugSymbols=false"
    )
}
$output = Join-Path $root "artifacts/$Runtime$suffix"
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
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if (-not $bash) {
        Write-Error "bash is required to strip native symbol tables"
        exit 1
    }
    & bash (Join-Path $PSScriptRoot "strip-native-symbols.sh") $output
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
$bash = Get-Command bash -ErrorAction SilentlyContinue
if (-not $bash) {
    Write-Error "bash is required to check the publish flavor"
    exit 1
}
& bash (Join-Path $PSScriptRoot "assert-publish-flavor.sh") $output $Flavor
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
Write-Host "Published $Runtime ($Flavor) to $output"
