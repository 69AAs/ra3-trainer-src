param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
# Run from the project root. All accesses are files; no process attachment.
$batches = @(
    @{ Name = 'coordinate-and-rank'; Args = @('0x1ed4a0', '0x35c200', 'xref=005ED4A0') },
    @{ Name = 'terrain'; Args = @('0xe7480', '0xe4120', '0xe7590', 'xref=004E7480', 'xref=004E7590') },
    @{ Name = 'callers'; Args = @('0xe7870', '0xeffc0', '0x1d46e0') },
    @{ Name = 'input-semantics'; Args = @('0x4e5140', '0xfe9b0', '0xe7870') }
)
foreach ($batch in $batches) {
    $inspectArgs = $batch.Args
    & $Dotnet run --project tools/HookAudit -c Release -- . --inspect --round17 @inspectArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Offline audit failed: $($batch.Name)" }
    Copy-Item -LiteralPath 'docs/round17-audit/inspect.txt' -Destination "docs/round17-audit/$($batch.Name).txt"
}
Get-FileHash -LiteralPath 'docs/round15-evidence/DUMP_1.12.game_20260929-222502.dmp' -Algorithm SHA256 |
    Format-List | Out-File -Encoding utf8 'docs/round17-audit/dump-sha256.txt'
