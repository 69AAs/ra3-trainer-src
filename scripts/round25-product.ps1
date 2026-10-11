param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Ra3Trainer.App/Ra3Trainer.App.csproj'
$output = Join-Path $root 'artifacts/round25-product'
$sdk = $null
foreach ($candidate in Get-Command dotnet -All) {
    $versions = & $candidate.Source --list-sdks
    if ($versions) { $sdk = $candidate.Source; break }
}
if (!$sdk) { throw 'Build requires the .NET 8 SDK. The published exe does not.' }
& $sdk publish $project -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Product publish failed' }
$exe = Join-Path $output 'Ra3Trainer.App.exe'
Get-FileHash -LiteralPath $exe -Algorithm SHA256
Write-Output 'Expected host events: osd-deployed, osd-status-slot, install, restore, osd-stop-request, osd-cleanup-complete/pending.'
Write-Output 'Expected native events: %TEMP%/Ra3Overlay-<game PID>.log: osd-proxy-loaded, osd-device-captured, osd-hook-installed, osd-frame-presented, osd-stop-disabled.'
Write-Output 'After exit: %LOCALAPPDATA%/Ra3Trainer/overlay-cleanup.log; no owned game d3d9.dll or deployment journal remains.'
if (!$BuildOnly) {
    # Explicit interactive product verification, not a background helper.
    & $exe
}
