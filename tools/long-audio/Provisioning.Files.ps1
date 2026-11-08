Set-StrictMode -Version Latest
function Assert-LAPhysical([string]$Path) {
    if(-not [IO.Path]::IsPathFullyQualified($Path)){throw 'LongAudioAbsolutePathRequired'}
    for($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)){
        if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'LongAudioLinkedPath'}
    }
}
function Get-LAHash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Read-LABytes([string]$Path,[long]$Maximum=8388608) {
    Assert-LAPhysical $Path
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -gt $Maximum){throw 'LongAudioEvidenceOversized'}
        $data=[byte[]]::new([int]$stream.Length);$offset=0
        while($offset -lt $data.Length){$count=$stream.Read($data,$offset,$data.Length-$offset);if($count -eq 0){throw 'LongAudioEvidenceIncomplete'};$offset += $count}
        if($stream.ReadByte() -ne -1){throw 'LongAudioEvidenceChanged'}
        return ,$data
    }finally{$stream.Dispose()}
}
function Assert-LAUnique($Element) {
    if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object){
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($p in $Element.EnumerateObject()){if(-not $seen.Add($p.Name)){throw 'LongAudioDuplicateField'};Assert-LAUnique $p.Value}
    }elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($v in $Element.EnumerateArray()){Assert-LAUnique $v}}
}
function ConvertFrom-LAJson([byte[]]$Bytes) {
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    if($text.StartsWith([string][char]0xfeff,[StringComparison]::Ordinal)){$text=$text.Substring(1)}
    $json=[Text.Json.JsonDocument]::Parse($text,[Text.Json.JsonDocumentOptions]@{MaxDepth=64})
    try{Assert-LAUnique $json.RootElement}finally{$json.Dispose()}
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 64
}
function ConvertTo-LABytes($Value) {
    # The pipeline emits nothing for an empty array. Preserve every existing nonempty serialized identity.
    if($Value -is [array] -and $Value.Count -eq 0){return ,([Text.Encoding]::UTF8.GetBytes('[]'))}
    return ,([Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Compress -Depth 64)))
}
function Assert-LARecord($Value,[string[]]$Fields) {
    if($Value -isnot [Collections.IDictionary] -or $Value.Count -ne $Fields.Count){throw 'LongAudioRecordShape'}
    foreach($key in $Value.Keys){if($key -cnotin $Fields){throw 'LongAudioUnknownField'}}
}
function Assert-LAHash([string]$Value) { if($Value -cnotmatch '^[a-f0-9]{64}$'){throw 'LongAudioHashShape'} }
function Assert-LAInteger($Value,[long]$Minimum=0) {
    if(($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum){throw 'LongAudioIntegerShape'}
}
function Write-LAAtomic([string]$Path,[byte[]]$Bytes,[switch]$Replace) {
    Assert-LAPhysical $Path;$pending=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.pending';Assert-LAPhysical $pending
    try{
        $stream=[IO.FileStream]::new($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        [IO.File]::Move($pending,$Path,[bool]$Replace)
    }finally{if(Test-Path -LiteralPath $pending){Assert-LAPhysical $pending;Remove-Item -LiteralPath $pending}}
}
function Set-LAExactFile([string]$Path,[byte[]]$Bytes) {
    if(Test-Path -LiteralPath $Path){if((Get-LAHash (Read-LABytes $Path)) -cne (Get-LAHash $Bytes)){throw 'LongAudioExistingFileChanged'};return}
    Write-LAAtomic $Path $Bytes
}
function Assert-LAClosed([string]$InstallRoot) {
    $prefix=(Join-Path $InstallRoot 'app')+[IO.Path]::DirectorySeparatorChar
    foreach($p in @(Get-Process CloudScribe -ErrorAction SilentlyContinue)){
        if(-not $p.Path){throw 'LongAudioProcessIdentityUnavailable'}
        if($p.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'LongAudioCloseThisInstallationFirst'}
    }
}
function Assert-LAUnconsumed($Context,$Plan) {
    if(Test-Path -LiteralPath $Context.Ledger){
        if((Get-LAHash (Read-LABytes $Context.Ledger)) -cne $Plan.InitialLedgerSha256){throw 'LongAudioCommitmentsMustBePreserved'}
    }
    foreach($name in @('operations','output')){
        $path=Join-Path $Context.Data $name;Assert-LAPhysical $path
        if((Test-Path -LiteralPath $path) -and @(Get-ChildItem -LiteralPath $path -Force).Count -gt 0){throw 'LongAudioOperationEvidenceMustBeReconciled'}
    }
    $allowed=@('budget-ledger.json','budget-ledger.json.lock','operations','output')
    foreach($entry in @(Get-ChildItem -LiteralPath $Context.Data -Force -ErrorAction SilentlyContinue)){
        if($entry.Name -cnotin $allowed){throw 'LongAudioUnrecognizedPriorEvidence'}
    }
}
