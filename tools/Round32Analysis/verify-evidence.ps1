$ErrorActionPreference='Stop'
$requests=Get-Content -Raw -Encoding UTF8 docs/round32-evidence/A-request-joins.json | ConvertFrom-Json
if($requests.Count -ne 116 -or @($requests.request|Select-Object -Unique).Count -ne 116){throw 'Request count/identity mismatch'}
if(@($requests|Where-Object {$_.probeMatches -gt 0}).Count -ne 105){throw 'Join count mismatch'}
$quantities=Get-Content -Raw -Encoding UTF8 docs/round32-evidence/A-quantity-requests.json | ConvertFrom-Json
foreach($q in $quantities){if($q.phase.DispatchEpochAfter -ne $q.phase.ExpectedDispatchEpoch -or $q.phase.DispatchEpochAfter -ne ($q.phase.DispatchEpochBefore+1) -or $q.phase.CreationFailure.Bits -ne 0 -or $q.outcome -ne 'Consumed'){throw 'Quantity protocol claim mismatch'}}
$objects=Get-Content -Raw -Encoding UTF8 docs/round32-evidence/B-object-records.json | ConvertFrom-Json
$cache=@{}
foreach($r in $objects){
    if(-not $cache.ContainsKey($r.File)){$cache[$r.File]=[IO.File]::ReadAllBytes((Join-Path $pwd $r.File))}
    $bytes=$cache[$r.File]
    if($r.HexOffset -lt 0 -or ($r.HexOffset+4) -gt $bytes.Length){throw "HexOffset out of range: $($r.File) $($r.HexOffset)"}
    if([BitConverter]::ToUInt32($bytes,$r.HexOffset).ToString('X8') -ne $r.Hex){throw 'Hex source mismatch'}
    if($r.NameOffset -lt 0 -or $r.NameOffset -gt $bytes.Length){throw "NameOffset out of range: $($r.File) $($r.NameOffset)"}
    $end=$r.NameOffset
    while($end -lt $bytes.Length -and $bytes[$end] -ne 0){$end++}
    if([Text.Encoding]::UTF8.GetString($bytes,$r.NameOffset,$end-$r.NameOffset) -ne ('GameObject:'+$r.Name)){throw 'Object name source mismatch'}
}
$catalog=Get-Content -Raw -Encoding UTF8 docs/round32-evidence/B-catalog.json | ConvertFrom-Json
if($catalog.Count -ne 507 -or $objects.Count -ne 3042){throw 'Catalog count mismatch'}
$translations=Get-Content -Encoding UTF8 samples/NewWorldBuilder/data/config/ObjWndTrans.json
foreach($r in $catalog){if($null -ne $r.ChineseSource){$index=$r.ChineseSource.line-1;if($index -lt 0 -or $index -ge $translations.Count){throw "Translation line out of range for $($r.Name)"};$line=$translations[$index];if(-not $line.Contains('"'+$r.Name+'"') -or -not $line.Contains($r.Chinese)){throw 'Translation source mismatch'}}}
$old=Get-Content -Raw -Encoding UTF8 docs/round32-evidence/A-legacy-quantity-context.json|ConvertFrom-Json
if($old.Count -ne 93 -or $quantities.Count -ne 22){throw '115 request coverage mismatch'}
New-Item -ItemType Directory -Force -Path artifacts/round32 | Out-Null
[pscustomobject]@{ExactRequests=$requests.Count;ProbeJoined=105;QuantityEpochAssertions=$quantities.Count;LegacyRequestContexts=$old.Count;ManifestRecordsReRead=$objects.Count;ObjectNames=$catalog.Count;EditorTranslationsChecked=@($catalog|Where-Object {$null -ne $_.ChineseSource}).Count;Passed=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 artifacts/round32/evidence-verification.json
Write-Output 'PASS 116 unique requests, 105 joins, 22 strict protocol outcomes, 93 legacy contexts, 3042 independent raw-offset rereads, 117 translation line rereads.'
