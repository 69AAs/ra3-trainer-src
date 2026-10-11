# Installs / removes the R3 map Lua helper next to an installed RA3 map.
# A map folder looks like:  Maps\<mapName>\<mapName>.map
# The helper goes to:       Maps\<mapName>\<mapName>\init.lua  (+ .lib_meta.json)
# It only ever creates that one subfolder: the .map itself is never modified.
param(
    [string]$Map = "",
    [switch]$All,
    [switch]$Uninstall,
    [switch]$List
)
$ErrorActionPreference = 'Stop'
$mapsRoot = Join-Path $env:APPDATA 'Red Alert 3\Maps'
$source   = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'init.lua'
if (-not (Test-Path $source)) { throw "init.lua not found next to this script: $source" }
if (-not (Test-Path $mapsRoot)) { throw "RA3 maps folder not found: $mapsRoot" }

function Get-LibTarget([string]$folder) {
    $map = Get-ChildItem -Path $folder -Filter *.map -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $map) { return $null }
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($map.Name)
    # The uninstall path is a recursive force-delete, so the derived directory must be a
    # plain name and a direct child of the map folder: a name like '..map' would otherwise
    # resolve to the map folder itself (or outside it).
    if (-not $stem -or $stem -eq '.' -or $stem -eq '..' -or $stem.Contains('\') -or $stem.Contains('/')) { return $null }
    $lib = Join-Path $folder $stem
    if ([System.IO.Path]::GetFullPath((Split-Path -Parent $lib)) -ne [System.IO.Path]::GetFullPath($folder)) { return $null }
    return @{ Folder = $folder; Stem = $stem; Lib = $lib }
}

function Write-Manifest([string]$libDir, [string]$stem) {
    $manifest = [ordered]@{
        FileName             = $stem
        FilePath             = $libDir
        FileType             = 'dir'
        IsEnabled            = $true
        IsIncluded           = $true
        RunOnce              = $true
        IsEvaluateEachFrame  = $true
        EvaluationInterval   = 1
        ActiveInEasy         = $true
        ActiveInMedium       = $true
        ActiveInHard         = $true
        OrderNum             = 99
        Children             = @([ordered]@{
            FileName            = 'init.lua'
            FilePath            = '\' + $stem + '\init.lua'
            FileType            = 'lua'
            IsEnabled           = $true
            IsIncluded          = $true
            RunOnce             = $true
            IsEvaluateEachFrame = $true
            EvaluationInterval  = 1
            ActiveInEasy        = $true
            ActiveInMedium      = $true
            ActiveInHard        = $true
            OrderNum            = 99
        })
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    # No BOM: PowerShell 5.1's -Encoding UTF8 writes one, and a BOM in front of the
    # manifest makes strict JSON readers (the game's own loader) fail on line 1.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $libDir '.lib_meta.json'), $json, $utf8NoBom)
}

$targets = @()
if ($All) {
    $targets = Get-ChildItem -Path $mapsRoot -Directory | ForEach-Object { Get-LibTarget $_.FullName } | Where-Object { $_ }
} elseif ($Map) {
    $folder = if (Test-Path $Map) { (Resolve-Path $Map).Path } else { Join-Path $mapsRoot $Map }
    if (-not (Test-Path $folder)) { throw "map folder not found: $folder" }
    $targets = @(Get-LibTarget $folder) | Where-Object { $_ }
} else {
    $targets = Get-ChildItem -Path $mapsRoot -Directory | ForEach-Object {
        $t = Get-LibTarget $_.FullName
        if ($t -and (Test-Path $t.Lib)) { $t }
    } | Where-Object { $_ }
}

if ($List) {
    # Reuse the target list that was already computed: -Map must list only the requested
    # map instead of re-enumerating every folder under the maps root.
    $listTargets = if ($Map) { $targets } else { Get-ChildItem -Path $mapsRoot -Directory | ForEach-Object { Get-LibTarget $_.FullName } | Where-Object { $_ } }
    foreach ($t in $listTargets) {
        $state = if (Test-Path $t.Lib) { 'installed' } else { '-' }
        '{0,-8} {1}' -f $state, $t.Folder
    }
    return
}

if ($targets.Count -eq 0) { Write-Host 'nothing to do (no map folder matched)'; return }

foreach ($t in $targets) {
    if ($Uninstall) {
        if (Test-Path $t.Lib) { Remove-Item -LiteralPath $t.Lib -Recurse -Force; Write-Host ("removed  " + $t.Lib) }
        else { Write-Host ("absent   " + $t.Lib) }
    } else {
        New-Item -ItemType Directory -Path $t.Lib -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $t.Lib 'init.lua') -Force
        Write-Manifest $t.Lib $t.Stem
        Write-Host ("installed " + $t.Lib)
    }
}
