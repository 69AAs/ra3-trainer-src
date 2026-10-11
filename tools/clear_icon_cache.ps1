
$ErrorActionPreference = "SilentlyContinue"
$me = (Get-Process -Id $PID).SessionId
$exList = @(Get-Process explorer)
"MY_SESSION=$me"
foreach ($e in $exList) { "EXPLORER_PID=$($e.Id) SESSION=$($e.SessionId) START=$($e.StartTime)" }
foreach ($a in @(Get-Process Ra3Trainer.App)) { "APP_PID=$($a.Id) START=$($a.StartTime) PATH=$($a.Path)" }
$same = $false
foreach ($e in $exList) { if ($e.SessionId -eq $me) { $same = $true } }
"SESSION_MATCH=$same"
if (-not $same) { "ABORT: agent session differs from explorer session; not restarting shell"; exit 9 }

$cacheDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Explorer"
$before = @()
$before += Get-ChildItem (Join-Path $env:LOCALAPPDATA "IconCache.db") | Select-Object -ExpandProperty FullName
$before += Get-ChildItem (Join-Path $cacheDir "iconcache*.db") | Select-Object -ExpandProperty FullName
$before += Get-ChildItem (Join-Path $cacheDir "thumbcache*.db") | Select-Object -ExpandProperty FullName
"BEFORE_FILES=$($before.Count)"

foreach ($e in $exList) { Stop-Process -Id $e.Id -Force }
Start-Sleep -Milliseconds 1200
"EXPLORER_STOPPED=" + (@(Get-Process explorer).Count)

$removed = 0; $failed = @()
foreach ($f in $before) {
  Remove-Item -LiteralPath $f -Force
  if (Test-Path -LiteralPath $f) { $failed += $f } else { $removed++ }
}
"REMOVED=$removed FAILED=$($failed.Count)"
foreach ($f in $failed) { "  LOCKED: $f" }

Start-Process explorer.exe
Start-Sleep -Seconds 3
foreach ($e in @(Get-Process explorer)) { "EXPLORER_BACK_PID=$($e.Id) SESSION=$($e.SessionId)" }
$after = @(Get-ChildItem (Join-Path $cacheDir "iconcache*.db")).Count
"ICONCACHE_REBUILDING=$after"
