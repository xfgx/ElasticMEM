# Verify the redacted exported ZIP using EXPORT.json, without extracting it.
param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
try {
    if($zip.Entries.Count -gt 10000){throw 'Too many ZIP entries.'}
    $names=@{};$total=0L
    foreach($e in $zip.Entries){
        if($e.FullName -match '(^/|\\|(^|/)\.\.(/|$)|:)'){throw 'Unsafe ZIP entry path.'}
        if($names.ContainsKey($e.FullName)){throw 'Duplicate ZIP entry.'}
        if($e.Length -gt 16MB){throw 'Exported log exceeds 16 MiB cap.'}
        $total+=$e.Length;if($total -gt 512MB){throw 'Log ZIP exceeds 512 MiB uncompressed cap.'}
        $names[$e.FullName]=$e
    }
    if(-not $names.ContainsKey('EXPORT.json')){throw 'Missing EXPORT.json.'}
    $reader=[IO.StreamReader]::new($names['EXPORT.json'].Open())
    try{$index=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    if($index.schema -ne 'elasticmem.logs.v1' -or $index.sensitive_data_included -ne $false){throw 'Not a logs-only ElasticMem export.'}
    $listed=@{}
    foreach($item in $index.files){
        if($item.path -eq 'EXPORT.json' -or $listed.ContainsKey($item.path) -or -not $names.ContainsKey($item.path)){throw 'Unsafe/duplicate/missing indexed entry.'}
        $e=$names[$item.path]
        if($e.Length -ne $item.bytes){throw "Size mismatch: $($item.path)"}
        $stream=$e.Open();$hash=[Security.Cryptography.SHA256]::Create()
        try{$actual=([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$hash.Dispose()}
        if($actual -ne $item.sha256){throw "SHA256 mismatch: $($item.path)"}
        $listed[$item.path]=$true
    }
    if($listed.Count+1 -ne $names.Count){throw 'ZIP has unindexed extra files.'}
    Write-Host "PASS: logs-only ZIP integrity, $($listed.Count) files. Review contents before sharing; integrity is not proof of RAM savings."
}finally{$zip.Dispose()}
