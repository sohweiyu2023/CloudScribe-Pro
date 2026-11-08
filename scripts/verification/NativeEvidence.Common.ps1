# Closed synthetic snapshots only. Never opens SQLite, reads vaults, launches apps, or deletes fixtures.
function Get-PhysicalFiles([string]$Root) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue((Resolve-PhysicalPath $Root))
    $files = [Collections.Generic.List[string]]::new()
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force -ErrorAction Stop) {
            Resolve-PhysicalPath $item.FullName | Out-Null
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) } else { $files.Add($item.FullName) }
        }
    }
    return @($files | Sort-Object)
}

function Assert-AppClosed {
    $running = @(Get-Process -ErrorAction Stop | Where-Object ProcessName -EQ 'CloudScribe')
    if ($running.Count) { throw 'CloudScribe is running. Close normally; no process will be terminated.' }
}

function Get-ClosedFixtureSnapshot([string]$Root) {
    Assert-AppClosed
    $files = @(Get-PhysicalFiles $Root)
    if (@($files | Where-Object { $_ -match '(?i)(-wal|-shm|-journal)$' }).Count) {
        throw 'Database sidecar present; a clean closed-database capture is not established.'
    }
    if (@($files | Where-Object { [IO.Path]::GetFileName($_) -match '(?i)credential|service.account|\.pem$|\.pfx$|\.key$' }).Count) {
        throw 'Potential credential material is not accepted in synthetic snapshot roots.'
    }
    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    $records = @()
    try {
        # Hold exclusive read handles across the complete capture. Locked/live files fail closed.
        foreach ($file in $files) {
            $handles.Add([IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None))
        }
        for ($i = 0; $i -lt $files.Count; $i++) {
            $records += [ordered]@{
                path = [IO.Path]::GetRelativePath($Root, $files[$i]); bytes = $handles[$i].Length
                sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($handles[$i])).ToLowerInvariant()
            }
        }
        Assert-AppClosed
        $after = @(Get-PhysicalFiles $Root)
        if (($files -join '|') -cne ($after -join '|')) { throw 'Fixture file set changed during capture.' }
        return $records
    } finally {
        foreach ($handle in $handles) { $handle.Dispose() }
    }
}

function Resolve-ManifestMember([string]$Root, [string]$RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or
        $RelativePath -match '[:*?]' -or [string]::IsNullOrWhiteSpace($RelativePath)) { throw 'Unsafe manifest member path.' }
    $full = Resolve-PhysicalPath (Join-Path $Root $RelativePath)
    if (-not $full.StartsWith($Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Manifest member escapes its root.'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Manifest member is not a file.' }
    return $full
}

function Test-HashedManifest([string]$Root, $Manifest) {
    if ($Manifest.schema_version -ne 1 -or @($Manifest.files).Count -eq 0) { throw 'Nonempty version 1 file manifest required.' }
    $seen = @{}
    foreach ($member in $Manifest.files) {
        $path = Resolve-ManifestMember $Root $member.path
        if ($seen.ContainsKey($path)) { throw 'Duplicate manifest member.' }
        $seen[$path] = $true
        if ($member.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $member.bytes -lt 0 -or
            (Get-Item -LiteralPath $path).Length -ne $member.bytes -or
            (Get-FileHash -LiteralPath $path).Hash -ne $member.sha256) { throw "Manifest identity mismatch: $($member.path)" }
    }
}

function New-NativeCase([string]$Id, [string]$Family, [string]$Scenario, [string]$Lane = 'script') {
    return [ordered]@{
        case_id = $Id; family_id = $Family; scenario = $Scenario; variant = 'default'; lane = $Lane
        status = 'NOT RUN'; expected = $Scenario; actual = $null; reason = 'No observation recorded.'
        started_utc = $null; finished_utc = $null; evidence_files = @(); dependent_case_ids = @()
    }
}

function Invoke-NativeCase($Case, [scriptblock]$Action) {
    $Case.started_utc = [DateTime]::UtcNow.ToString('o')
    try {
        $Case.actual = & $Action
        $Case.status = 'PASS'; $Case.reason = 'The stated script assertion was checked.'
    } catch {
        $Case.status = if ($_.Exception.Message.StartsWith('FAIL:')) { 'FAIL' } else { 'BLOCKED' }
        $Case.reason = $_.Exception.Message
    }
    $Case.finished_utc = [DateTime]::UtcNow.ToString('o')
}
