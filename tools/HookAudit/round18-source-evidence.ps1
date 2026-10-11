param([string]$Root = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path
$outDir = Join-Path $Root 'docs/round18-audit'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$reportPath = Join-Path $Root 'src/Ra3Trainer.Core/Assets/trainer_report.json'
$scriptPath = Join-Path $Root 'ra3-original-script.aa'
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$original = (Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8).Replace("`r`n", "`n").Trim()
if (-not $report.auto_assembler_scripts -or @($report.auto_assembler_scripts).Count -lt 1 -or -not $report.auto_assembler_scripts[0].script) { throw 'Decrypted report has no auto_assembler_scripts[0].script' }
$extracted = $report.auto_assembler_scripts[0].script.Replace("`r`n", "`n").Trim()
if ($original -cne $extracted) { throw 'Original AA differs from decrypted report script' }
$notes = [System.Collections.Generic.List[string]]::new()
$notes.Add('Offline only: local decrypted script, JSON manifest and captured .text fixture; no process APIs.')
$notes.Add('Original AA equals auto_assembler_scripts[0].script after newline normalization and outer whitespace trim.')
foreach ($path in @($scriptPath, $reportPath, (Join-Path $Root 'tests/Ra3Trainer.Tests/Fixtures/round15-text.bin.gz'))) {
    $notes.Add("SHA256 $((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) $path")
}
$notes.Add('Independent Free Build script:')
$freeBuild = @($report.auto_assembler_scripts | Where-Object feature_name -eq 'Free Build')
if ($freeBuild.Count -ne 1 -or -not $freeBuild[0].script) { throw "Expected exactly one 'Free Build' auto-assembler script, found $($freeBuild.Count)" }
$notes.Add($freeBuild[0].script)
$notes.Add('patch_manifest.hooks (Free Build target is absent):')
if (-not $report.patch_manifest -or -not $report.patch_manifest.hooks) { throw 'Decrypted report has no patch_manifest.hooks' }
foreach ($hook in $report.patch_manifest.hooks) {
    $notes.Add("$($hook.address) -> $($hook.trampoline_target); restore: $($hook.original_assembly -join '; ')")
}
$lines = $original.Split("`n")
foreach ($range in @(@(570,670), @(992,1141))) {
    for ($i=$range[0]-1; $i -lt [Math]::Min($range[1],$lines.Length); $i++) {
        $notes.Add(('{0}: {1}' -f ($i+1),$lines[$i]))
    }
}
$inputStream = [IO.File]::OpenRead((Join-Path $Root 'tests/Ra3Trainer.Tests/Fixtures/round15-text.bin.gz'))
try {
    $gzip = [IO.Compression.GZipStream]::new($inputStream,[IO.Compression.CompressionMode]::Decompress)
    try {
        $output = [IO.MemoryStream]::new()
        try { $gzip.CopyTo($output); $code=$output.ToArray() } finally { $output.Dispose() }
    } finally { $gzip.Dispose() }
} finally { $inputStream.Dispose() }
$notes.Add('Captured restored .text fixture starts at RVA 1000; following addresses are game RVAs, base 00400000.')
foreach ($entry in @(@(0xE7563,16), @(0xE7590,3), @(0xE75D9,5), @(0xE760E,8), @(0xE782B,8), @(0x1ED4A0,6), @(0x1ED800,9), @(0x205240,16), @(0x35C200,8))) {
    $start = $entry[0] - 0x1000
    $end = $start + $entry[1] - 1
    if ($start -lt 0 -or $end -ge $code.Length) { throw ('Restored .text fixture too short for game+{0:X}: need {1} bytes, have {2}' -f $entry[0], ($end + 1), $code.Length) }
    $bytes = [byte[]]$code[$start..$end]
    $notes.Add(('game+{0:X}: {1}' -f $entry[0],([BitConverter]::ToString($bytes).Replace('-',' '))))
}
$notes | Set-Content -LiteralPath (Join-Path $outDir 'original-source.txt') -Encoding UTF8
$notes
