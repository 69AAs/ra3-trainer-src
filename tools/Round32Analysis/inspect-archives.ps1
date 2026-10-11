$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force -Path docs/round32-evidence | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$wer = [Collections.Generic.List[object]]::new()
$records = foreach($path in Get-ChildItem samples/evening -Filter '*.zip') {
    $zip=[IO.Compression.ZipFile]::OpenRead($path.FullName)
    try {
        foreach($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }   # directory entries cannot be opened/hashed
            if ($entry.FullName.EndsWith('/Report.wer')) {
                $reader=[IO.StreamReader]::new($entry.Open())
                try { $lines=$reader.ReadToEnd() -split "`n" } finally { $reader.Dispose() }
                for($i=0;$i -lt $lines.Count;$i++) {
                    if($lines[$i] -match '^EventTime=(\d+)') {
                        try { $utc=[DateTimeOffset]::new([DateTime]::FromFileTimeUtc([long]$Matches[1])) } catch { continue }   # corrupt FILETIME: skip entry, never abort the scan
                        $wer.Add([pscustomobject]@{Archive=$path.Name;Entry=$entry.FullName;Line=$i+1;EventTime=$Matches[1];BeijingTime=$utc.ToOffset([TimeSpan]::FromHours(8)).ToString('o')})
                    }
                }
            }
            $stream=$entry.Open()
            try {
                $hash=[Security.Cryptography.SHA256]::Create()
                try { $hex=([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','') }
                finally { $hash.Dispose() }
            } finally { $stream.Dispose() }
            [pscustomobject]@{Archive=$path.Name;Entry=$entry.FullName;Bytes=$entry.Length;Sha256=$hex}
        }
    } finally { $zip.Dispose() }
}
ConvertTo-Json -InputObject @($records) -Depth 4 | Set-Content -Encoding UTF8 docs/round32-evidence/A-archive-inventory.json
ConvertTo-Json -InputObject @($wer) -Depth 4 | Set-Content -Encoding UTF8 docs/round32-evidence/A-wer-audit.json
Write-Output "Archive entries=$($records.Count); WER event times=$($wer.Count)"
