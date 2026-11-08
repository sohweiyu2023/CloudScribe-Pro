# Provider-free helpers. Importing this file has no external side effects.
Set-StrictMode -Version Latest

function Resolve-PhysicalPath([string]$Path, [switch]$AllowMissingLeaf) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'An absolute path is required.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq [IO.Path]::GetPathRoot($Path).TrimEnd('\', '/')) { throw 'A filesystem root is not allowed.' }
    $current = $full
    $first = $true
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $current" }
        } elseif (-not ($first -and $AllowMissingLeaf)) { throw "Path does not exist: $current" }
        $first = $false
        $current = Split-Path -Parent $current
    }
    return $full
}

function Test-PathOverlap([string]$Left, [string]$Right) {
    $a = [IO.Path]::GetFullPath($Left).TrimEnd('\', '/')
    $b = [IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith($b + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith($a + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function New-EvidenceDirectory([string]$Path, [string[]]$ProtectedRoots) {
    $full = Resolve-PhysicalPath $Path -AllowMissingLeaf
    foreach ($root in $ProtectedRoots) {
        if (Test-PathOverlap $full $root) { throw "Evidence path overlaps protected root: $root" }
    }
    if (Test-Path -LiteralPath $full) { throw 'Evidence destination must be new; nothing is overwritten.' }
    [IO.Directory]::CreateDirectory($full) | Out-Null
    return $full
}

function Write-EvidenceJson([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 -ErrorAction Stop
}

function Invoke-LoggedProcess([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [string]$LogBase) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $stdout = [IO.File]::Create($LogBase + '.stdout.log')
    $stderr = [IO.File]::Create($LogBase + '.stderr.log')
    try {
        if (-not $process.Start()) { throw "Could not start: $File" }
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $process.WaitForExit()
        $null = $outTask.GetAwaiter().GetResult()
        $null = $errTask.GetAwaiter().GetResult()
        return $process.ExitCode
    } finally {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
    }
}

function New-CheckStage([string]$Id, [string]$File, [string[]]$Arguments, [string]$WorkingDirectory,
    [string[]]$DependsOn = @(), [string]$SuccessText = '', [switch]$Prerequisite) {
    return [ordered]@{
        id = $Id; status = 'NOT RUN'; reason = 'Not attempted.'; started_utc = $null; finished_utc = $null
        executable = $File; arguments = @($Arguments); working_directory = $WorkingDirectory
        depends_on = @($DependsOn); success_text = $SuccessText; prerequisite = [bool]$Prerequisite
        exit_code = $null; logs = @(); skip_lines = @(); summary_lines = @()
    }
}

function Invoke-CheckStages([object[]]$Stages, [string]$OutputDirectory, [scriptblock]$Checkpoint) {
    $known = @{}
    foreach ($stage in $Stages) {
        if ($known.ContainsKey($stage.id)) { throw "Duplicate stage: $($stage.id)" }
        foreach ($dependency in $stage.depends_on) {
            if (-not $known.ContainsKey($dependency)) { throw "Dependency must precede stage: $dependency" }
        }
        $blocked = @($stage.depends_on | Where-Object { $known[$_].status -ne 'PASS' })
        if ($blocked.Count) {
            $stage.status = 'BLOCKED'
            $stage.reason = 'Unsuccessful prerequisite(s): ' + ($blocked -join ', ')
        } else {
            $stage.started_utc = [DateTime]::UtcNow.ToString('o')
            $stage.reason = 'Execution interrupted or not completed.'
            & $Checkpoint
            $base = Join-Path $OutputDirectory $stage.id
            $stage.logs = @($stage.id + '.stdout.log', $stage.id + '.stderr.log')
            try {
                $stage.exit_code = Invoke-LoggedProcess $stage.executable $stage.arguments $stage.working_directory $base
                $lines = @(Get-Content -LiteralPath ($base + '.stdout.log'), ($base + '.stderr.log'))
                $stage.skip_lines = @($lines | Where-Object { $_ -match '(?i)\bskip(?:ped)?\b' })
                $stage.summary_lines = @($lines | Where-Object { $_ -match '(?i)passed:|failed:|skipped:|total:|^Ran \d+ tests|^OK\b|^FAILED\b' })
                if ($stage.exit_code -ne 0) {
                    $stage.status = if ($stage.prerequisite) { 'BLOCKED' } else { 'FAIL' }
                    $stage.reason = "Child process exited $($stage.exit_code); see both logs."
                } elseif ($stage.success_text -and -not (($lines -join [Environment]::NewLine).Contains($stage.success_text))) {
                    $stage.status = 'BLOCKED'
                    $stage.reason = 'Required full-validation success message absent; exit zero alone is insufficient.'
                } else {
                    $stage.status = 'PASS'; $stage.reason = 'Command completed with exit zero and required success evidence.'
                }
            } catch {
                $stage.status = 'BLOCKED'; $stage.reason = $_.Exception.Message
            }
            $stage.finished_utc = [DateTime]::UtcNow.ToString('o')
        }
        $known[$stage.id] = $stage
        Write-Host "$($stage.id): $($stage.status)"
        & $Checkpoint
    }
}

function Get-EvidenceExitCode([object[]]$Records) {
    if (@($Records | Where-Object status -EQ 'FAIL').Count) { return 1 }
    if (@($Records | Where-Object { $_.status -notin @('PASS', 'NOT APPLICABLE') }).Count) { return 2 }
    return 0
}

function Write-ArtifactManifest([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Where-Object Name -NE 'artifacts.json' |
        Sort-Object FullName | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($Directory, $_.FullName); bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    Write-EvidenceJson (Join-Path $Directory 'artifacts.json') $files
}
