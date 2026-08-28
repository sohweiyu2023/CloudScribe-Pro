$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'CloudScribe verification requires PowerShell 7 or later.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'Windows runtime screenshot capture must run on Windows.'
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw 'Python is required for physical output-path and visual-evidence validation.'
}
if ($args.Count -gt 1) {
    throw 'Usage: capture-stage2-windows.ps1 [empty-output-directory]'
}
$outputCandidate = if ($args.Count -eq 1) {
    [string]$args[0]
}
else {
    Join-Path ([IO.Path]::GetTempPath()) ("cloudscribe-stage2-runtime-screenshots-" + [Guid]::NewGuid().ToString('N'))
}
$preparedOutput = & python tools/prepare_physical_directory.py $outputCandidate `
    --label 'Stage 2 screenshot output directory' --forbid-root $repoRoot --require-empty
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 2 screenshot output directory failed physical-path validation.'
}
$outputDir = ([string]($preparedOutput | Select-Object -Last 1)).Trim()
if ([string]::IsNullOrWhiteSpace($outputDir)) {
    throw 'Physical directory preparation did not return a screenshot output path.'
}

$dataCandidate = Join-Path ([IO.Path]::GetTempPath()) ("cloudscribe-stage2-data-" + [Guid]::NewGuid().ToString('N'))
$preparedData = & python tools/prepare_physical_directory.py $dataCandidate `
    --label 'Stage 2 temporary data directory' --forbid-root $repoRoot --require-empty
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 2 temporary data directory failed physical-path validation.'
}
$dataRoot = ([string]($preparedData | Select-Object -Last 1)).Trim()
$overrideName = 'CLOUDSCRIBE_CloudScribe__AppDataDirectoryOverride'
$sourceHashName = 'CLOUDSCRIBE_SOURCE_MANIFEST_SHA256'
$captureModeName = 'CLOUDSCRIBE_STAGE2_CAPTURE_MODE'
$captureDirectoryName = 'CLOUDSCRIBE_STAGE2_CAPTURE_DIR'
$previousOverride = [Environment]::GetEnvironmentVariable($overrideName, [EnvironmentVariableTarget]::Process)
$previousSourceHash = [Environment]::GetEnvironmentVariable($sourceHashName, [EnvironmentVariableTarget]::Process)
$previousCaptureMode = [Environment]::GetEnvironmentVariable($captureModeName, [EnvironmentVariableTarget]::Process)
$previousCaptureDirectory = [Environment]::GetEnvironmentVariable($captureDirectoryName, [EnvironmentVariableTarget]::Process)
try {
    python tools/update_sha256_manifest.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Repository SHA-256 manifest does not match the current source bytes.' }
    [Environment]::SetEnvironmentVariable(
        $sourceHashName,
        (Get-FileHash (Join-Path $repoRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant(),
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable($captureModeName, '1', [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $captureDirectoryName,
        $outputDir,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $overrideName,
        (Join-Path $dataRoot 'appdata'),
        [EnvironmentVariableTarget]::Process)
    $stdoutPath = Join-Path $outputDir 'application.stdout.log'
    $stderrPath = Join-Path $outputDir 'application.stderr.log'
    & python tools/run_bounded_process.py `
        --timeout-seconds 60 `
        --max-output-bytes 8388608 `
        --stdout-file $stdoutPath `
        --stderr-file $stderrPath `
        -- dotnet run `
            --project src/CloudScribe.App/CloudScribe.App.csproj `
            -c Release `
            --no-build `
            --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Stage 2 screenshot application failed through the bounded runner with exit code $LASTEXITCODE."
    }
    if (Test-Path (Join-Path $outputDir 'capture-error.txt')) {
        throw (Get-Content (Join-Path $outputDir 'capture-error.txt') -Raw)
    }
    if (-not (Test-Path (Join-Path $outputDir 'visual-evidence-manifest.json'))) {
        throw 'Visual evidence manifest was not created.'
    }
    python tools/verify_stage2_visual_evidence.py $outputDir
    if ($LASTEXITCODE -ne 0) {
        throw 'Stage 2 visual evidence validation failed.'
    }
    Write-Host "Stage 2 runtime screenshot evidence retained at: $outputDir"
}
finally {
    [Environment]::SetEnvironmentVariable(
        $captureDirectoryName,
        $previousCaptureDirectory,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $captureModeName,
        $previousCaptureMode,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $overrideName,
        $previousOverride,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $sourceHashName,
        $previousSourceHash,
        [EnvironmentVariableTarget]::Process)
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
}
