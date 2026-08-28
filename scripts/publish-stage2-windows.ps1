[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string] $Configuration,
    [Parameter(Mandatory = $true)][ValidateSet('development-candidate', 'verification-pending')][string] $Status
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'CloudScribe publishing requires PowerShell 7 or later.'
}
if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'CloudScribe Windows publishing must run on Windows.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found.'
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw 'Python is required for physical output-path validation.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$prepared = & python tools/prepare_physical_directory.py $OutputDirectory `
    --label 'CloudScribe Windows runnable output directory' --forbid-root $repoRoot --require-empty
if ($LASTEXITCODE -ne 0) {
    throw 'CloudScribe Windows runnable output directory failed physical-path validation.'
}
$output = ([string]($prepared | Select-Object -Last 1)).Trim()
if ([string]::IsNullOrWhiteSpace($output)) {
    throw 'Physical directory preparation did not return a runnable output path.'
}

& dotnet publish src/CloudScribe.App/CloudScribe.App.csproj `
    -c $Configuration `
    --no-build `
    --no-restore `
    --disable-build-servers `
    -m:1 `
    -nodeReuse:false `
    -p:BuildProjectReferences=false `
    -p:BuildInParallel=false `
    -p:UseSharedCompilation=false `
    --output $output
if ($LASTEXITCODE -ne 0) {
    throw "CloudScribe $Configuration publish failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    'CloudScribe.exe',
    'CloudScribe.dll',
    'CloudScribe.deps.json',
    'CloudScribe.runtimeconfig.json',
    'appsettings.json'
)
$missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $output $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "Published CloudScribe output is incomplete: $($missing -join ', ')"
}

$logsDirectory = Join-Path $output 'logs'
$preparedLogs = & python tools/prepare_physical_directory.py $logsDirectory `
    --label 'CloudScribe executable-local logs directory' --forbid-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw 'CloudScribe executable-local logs directory failed physical-path validation.'
}

$repositoryVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'SESSION_STATE.json') -Raw | ConvertFrom-Json).repository_version
$marker = @(
    'CloudScribe Pro Windows development output',
    "repository_version=$repositoryVersion",
    "configuration=$Configuration",
    "status=$Status",
    "created_at_utc=$([DateTimeOffset]::UtcNow.ToString('o'))",
    'This executable is a development checkpoint until the complete Stage 2 verifier passes.',
    'Application runtime logs are written to the logs folder beside CloudScribe.exe.',
    'Build and verification logs are mirrored to logs\\build when the launcher exits.'
)
Set-Content -LiteralPath (Join-Path $output 'BUILD-STATUS.txt') -Value $marker -Encoding utf8
Set-Content -LiteralPath (Join-Path $output 'RUN-CLOUDSCRIBE.cmd') -Value @(
    '@echo off',
    'start "" "%~dp0CloudScribe.exe"'
) -Encoding ascii

Write-Host "CloudScribe runnable output: $output" -ForegroundColor Green
Write-Host "CloudScribe executable: $(Join-Path $output 'CloudScribe.exe')" -ForegroundColor Green
Write-Host "Runtime logs: $logsDirectory" -ForegroundColor Yellow
