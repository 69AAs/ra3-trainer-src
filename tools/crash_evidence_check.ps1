# Crash-evidence check for the RA3 trainer workflow (read-only).
# Judgement criteria (from skill ra3-game-engine-hooks):
#   1) Windows event log Application Error: module name + offset
#   2) new DUMP_*.dmp under the game Data dir
#   3) whether the game froze (user report)
param([string]$Since = "2026-10-07 00:00:00")

$start = [datetime]::Parse($Since)
Write-Host "=== Application Error / WER events since $start ==="
try {
    Get-WinEvent -FilterHashtable @{LogName="Application"; StartTime=$start} -ErrorAction Stop |
        Where-Object { $_.ProviderName -match "Application Error|Windows Error Reporting|Application Hang" } |
        Select-Object -First 40 TimeCreated, ProviderName, Id |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
} catch { Write-Host "no matching events / log unreadable: $($_.Exception.Message)" }

Write-Host "=== any Application-log message mentioning ra3 since $start ==="
try {
    Get-WinEvent -FilterHashtable @{LogName="Application"; StartTime=$start} -ErrorAction Stop |
        Where-Object { $_.Message -match "ra3" } |
        Select-Object -First 20 TimeCreated, ProviderName, @{n="head"; e={ ($_.Message -split "`r?`n")[0] }} |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
} catch { Write-Host "no matching events: $($_.Exception.Message)" }

Write-Host "=== newest DUMP_*.dmp in the game Data dir ==="
$data = "D:\Games\Red Alert 3\Data"
if (Test-Path $data) {
    Get-ChildItem -Path $data -Filter "DUMP_*.dmp" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 8 LastWriteTime, @{n="GB"; e={ [math]::Round($_.Length / 1GB, 2) }}, Name |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
    $new = Get-ChildItem -Path $data -Filter "DUMP_*.dmp" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $start }
    Write-Host ("DUMPs created since {0}: {1}" -f $start, $new.Count)
} else { Write-Host "game Data dir not found: $data" }

Write-Host "=== trainer-side crash dumps / evidence (if any) ==="
$ever = "D:\Games\Ra3Trainer\build-test\crashdumps"
if (Test-Path $ever) {
    Get-ChildItem -Path $ever -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 10 LastWriteTime, FullName |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
} else { Write-Host "no crashdumps dir at $ever" }
