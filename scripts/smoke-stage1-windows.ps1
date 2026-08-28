$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'CloudScribe verification requires PowerShell 7 or later.'
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
$RuntimeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cloudscribe-stage1-smoke-" + [Guid]::NewGuid().ToString('N'))
$overrideName = 'CLOUDSCRIBE_CloudScribe__AppDataDirectoryOverride'
$previousOverride = [Environment]::GetEnvironmentVariable($overrideName, [EnvironmentVariableTarget]::Process)
$AppOutputRoot = Join-Path $RepoRoot 'src/CloudScribe.App/bin'
if (-not (Test-Path -LiteralPath $AppOutputRoot -PathType Container)) {
    throw "CloudScribe build output directory does not exist: $AppOutputRoot"
}
$releaseCandidates = @(
    Get-ChildItem -LiteralPath $AppOutputRoot -Filter 'CloudScribe.exe' -File -Recurse |
        Where-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($AppOutputRoot, $_.FullName)
            $segments = $relativePath -split '[\\/]'
            ($segments -contains 'Release') -and ($segments -contains 'net10.0')
        }
)
if ($releaseCandidates.Count -ne 1) {
    $candidateList = if ($releaseCandidates.Count -eq 0) {
        '<none>'
    }
    else {
        ($releaseCandidates.FullName -join [Environment]::NewLine)
    }
    throw "Expected exactly one Release/net10.0 CloudScribe.exe beneath $AppOutputRoot, found $($releaseCandidates.Count):$([Environment]::NewLine)$candidateList"
}
$App = $releaseCandidates[0].FullName
Write-Host "Resolved Stage 1 Windows smoke executable: $App"

New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
$Primary = $null
$primaryTerminated = $true
$SmokePassed = $false
[Environment]::SetEnvironmentVariable(
    $overrideName,
    (Join-Path $RuntimeRoot 'appdata'),
    [EnvironmentVariableTarget]::Process)
try {
    $Primary = Start-Process -FilePath $App -PassThru
    Start-Sleep -Seconds 4
    if ($Primary.HasExited) {
        throw 'Primary CloudScribe shell exited before the smoke-test observation window.'
    }

    $Secondary = Start-Process -FilePath $App -ArgumentList 'sample-document.txt' -PassThru
    if (-not $Secondary.WaitForExit(8000)) {
        $Secondary.Kill($true)
        if (-not $Secondary.WaitForExit(5000)) {
            throw 'Secondary activation process did not terminate within five seconds after forced cancellation.'
        }
        throw 'Secondary activation process exceeded the 8-second runtime bound.'
    }
    if ($Secondary.ExitCode -ne 0) {
        throw "Secondary activation process exited with code $($Secondary.ExitCode)."
    }

    Start-Sleep -Seconds 2
    $DiagnosticDirectory = Join-Path $RuntimeRoot 'appdata/logs'
    $DiagnosticLog = $null
    $Content = $null
    $diagnosticDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        if (Test-Path -LiteralPath $DiagnosticDirectory -PathType Container) {
            foreach ($candidate in @(Get-ChildItem -LiteralPath $DiagnosticDirectory -Filter 'cloudscribe-*.jsonl' -File | Sort-Object LastWriteTimeUtc)) {
                try {
                    $candidateContent = Get-Content -LiteralPath $candidate.FullName -Raw -ErrorAction Stop
                }
                catch [System.IO.IOException] {
                    continue
                }
                catch [System.UnauthorizedAccessException] {
                    continue
                }
                if ($candidateContent.Contains('"EventName":"ApplicationReady"', [System.StringComparison]::Ordinal) -and
                    $candidateContent.Contains('"EventName":"ActivationReceived"', [System.StringComparison]::Ordinal)) {
                    $DiagnosticLog = $candidate
                    $Content = $candidateContent
                    break
                }
            }
        }
        if (-not $DiagnosticLog) {
            Start-Sleep -Milliseconds 100
        }
    } while (-not $DiagnosticLog -and [DateTimeOffset]::UtcNow -lt $diagnosticDeadline)
    if (-not $DiagnosticLog -or $null -eq $Content) {
        throw "No primary structured diagnostic log containing ApplicationReady and ActivationReceived was observed under the configured AppData override logs directory: $DiagnosticDirectory"
    }
    if (-not (Test-Path (Join-Path $RuntimeRoot 'appdata/data/cloudscribe.db'))) { throw 'SQLite database was not created.' }

    $SmokePassed = $true
    Write-Host 'PASS: Stage 1 Windows offline UI launch, SQLite initialization, and secondary activation routing.'

}
finally {
    if ($Primary -and -not $Primary.HasExited) {
        $Primary.Kill($true)
        $primaryTerminated = $Primary.WaitForExit(5000)
    }
    [Environment]::SetEnvironmentVariable(
        $overrideName,
        $previousOverride,
        [EnvironmentVariableTarget]::Process)
    if ($primaryTerminated -and $SmokePassed) {
        Remove-Item $RuntimeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($primaryTerminated) {
        Write-Warning "Stage 1 Windows smoke evidence retained after failure at $RuntimeRoot."
    }
    else {
        Write-Error "Primary CloudScribe process did not terminate within five seconds; runtime evidence is retained at $RuntimeRoot."
    }
}
