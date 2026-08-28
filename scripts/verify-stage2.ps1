$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'CloudScribe verification requires PowerShell 7 or later.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$env:Platform = 'Any CPU'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDTERMINALLOGGER = 'off'
$env:NO_COLOR = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$ProgressPreference = 'SilentlyContinue'
if ($null -ne $PSStyle) {
    $PSStyle.OutputRendering = [System.Management.Automation.OutputRendering]::PlainText
}

function Remove-GeneratedBuildState {
    Get-ChildItem -Path . -Directory -Recurse -Force |
        Where-Object { $_.Name -in @('bin', 'obj', 'TestResults', '__pycache__', '.vs') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force
}

function Assert-NativeSuccess([string] $Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with native exit code $LASTEXITCODE."
    }
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'This promotion verifier requires Windows runtime evidence. Use scripts/verify-stage2.sh on Linux/Xvfb.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The pinned .NET SDK is required for Stage 2 promotion; dotnet was not found.'
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw 'Python is required for Stage 2 source, package-scan, and screenshot evidence validation.'
}

$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$evidenceCandidate = if (-not [string]::IsNullOrWhiteSpace($env:CLOUDSCRIBE_STAGE2_EVIDENCE_DIR)) {
    $env:CLOUDSCRIBE_STAGE2_EVIDENCE_DIR
}
else {
    Join-Path (Split-Path $repoRoot -Parent) "CloudScribe_Stage2_Runtime_Evidence_$stamp"
}
$preparedEvidence = & python tools/prepare_physical_directory.py $evidenceCandidate `
    --label 'Stage 2 evidence directory' --forbid-root $repoRoot --require-empty
Assert-NativeSuccess 'Stage 2 evidence directory preparation'
$evidenceRoot = ([string]($preparedEvidence | Select-Object -Last 1)).Trim()
if ([string]::IsNullOrWhiteSpace($evidenceRoot)) {
    throw 'Physical directory preparation did not return a Stage 2 evidence path.'
}
$scanOutput = Join-Path $evidenceRoot 'package-scans'
$visualOutput = Join-Path $evidenceRoot 'visual'
$testOutput = Join-Path $evidenceRoot 'test-results'
$logOutput = Join-Path $evidenceRoot 'logs'
foreach ($directory in @($scanOutput, $visualOutput, $testOutput, $logOutput)) {
    & python tools/prepare_physical_directory.py $directory `
        --label 'Stage 2 evidence child directory' --forbid-root $repoRoot | Out-Null
    Assert-NativeSuccess "physical directory preparation for $directory"
}
$transcriptStarted = $false
$verificationStartedAt = [DateTimeOffset]::UtcNow
$commandLedgerPath = Join-Path $logOutput 'command-ledger.jsonl'
$script:CommandSequence = 0
$script:QuietCommandSequence = 0
$script:RunnableExecutable = $null
$script:RunnableLogsDirectory = $null
$script:RunnableStatus = $null
$deliveryPointer = $null

Write-Host ''
Write-Host 'CloudScribe Stage 2 full verification' -ForegroundColor Cyan
Write-Host "Repository: $repoRoot"
Write-Host "Evidence root: $evidenceRoot" -ForegroundColor Yellow
Write-Host "Per-command stdout/stderr: $logOutput"
Write-Host 'Every command below reports START, PASS or FAIL with duration and log paths.'
Write-Host 'Enhanced .NET Terminal Logger output is disabled to keep Command Prompt output stable and readable.'
Write-Host ''

function Convert-CommandForDisplay {
    param([Parameter(Mandatory = $true)][string[]] $Command)
    return (($Command | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    }) -join ' ')
}

function Write-CommandLedgerRecord {
    param(
        [Parameter(Mandatory = $true)][int] $Sequence,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $CommandText,
        [Parameter(Mandatory = $true)][DateTimeOffset] $StartedAt,
        [Parameter(Mandatory = $true)][DateTimeOffset] $CompletedAt,
        [Parameter(Mandatory = $true)][string] $Status,
        [Parameter(Mandatory = $true)][int] $ExitCode,
        [Parameter(Mandatory = $true)][string] $StdoutPath,
        [Parameter(Mandatory = $true)][string] $StderrPath,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds,
        [string] $Failure = $null
    )
    $record = [ordered]@{
        sequence = $Sequence
        label = $Label
        command = $CommandText
        working_directory = $repoRoot
        started_at_utc = $StartedAt.ToString('o')
        completed_at_utc = $CompletedAt.ToString('o')
        duration_seconds = [Math]::Round(($CompletedAt - $StartedAt).TotalSeconds, 3)
        status = $Status
        exit_code = $ExitCode
        timeout_seconds = $TimeoutSeconds
        stdout_path = $StdoutPath
        stderr_path = $StderrPath
        failure = $Failure
    }
    $record | ConvertTo-Json -Compress | Add-Content -LiteralPath $commandLedgerPath -Encoding utf8
}

function Invoke-BoundedCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string[]] $Command,
        [Parameter(Mandatory = $true)][string] $StdoutPath,
        [Parameter(Mandatory = $true)][string] $StderrPath,
        [int] $TimeoutSeconds = 1200,
        [int] $MaximumOutputBytes = 67108864,
        [switch] $Tee
    )
    $script:CommandSequence++
    $sequence = $script:CommandSequence
    $startedAt = [DateTimeOffset]::UtcNow
    $commandText = Convert-CommandForDisplay $Command
    Write-Host ("[{0:HH:mm:ss}] STEP {1:00} START  {2}" -f [DateTimeOffset]::Now, $sequence, $Label) -ForegroundColor Cyan
    Write-Host "  command: $commandText"
    Write-Host "  stdout:  $StdoutPath"
    Write-Host "  stderr:  $StderrPath"

    $runnerArguments = @(
        'tools/run_bounded_process.py',
        '--timeout-seconds', [string]$TimeoutSeconds,
        '--max-output-bytes', [string]$MaximumOutputBytes,
        '--stdout-file', $StdoutPath,
        '--stderr-file', $StderrPath
    )
    if ($Tee) { $runnerArguments += '--tee' }
    $runnerArguments += '--'
    $runnerArguments += $Command

    try {
        & python @runnerArguments
        $nativeExitCode = if ($null -eq $LASTEXITCODE) { -1 } else { [int]$LASTEXITCODE }
        if ($nativeExitCode -ne 0) {
            throw "$Label failed with native exit code $nativeExitCode."
        }
        $completedAt = [DateTimeOffset]::UtcNow
        Write-CommandLedgerRecord -Sequence $sequence -Label $Label -CommandText $commandText `
            -StartedAt $startedAt -CompletedAt $completedAt -Status 'passed' -ExitCode 0 `
            -StdoutPath $StdoutPath -StderrPath $StderrPath -TimeoutSeconds $TimeoutSeconds
        Write-Host ("[{0:HH:mm:ss}] STEP {1:00} PASS   {2:N1}s — {3}" -f [DateTimeOffset]::Now, $sequence, ($completedAt - $startedAt).TotalSeconds, $Label) -ForegroundColor Green
    }
    catch {
        $completedAt = [DateTimeOffset]::UtcNow
        $nativeExitCode = if ($null -eq $LASTEXITCODE) { -1 } else { [int]$LASTEXITCODE }
        Write-CommandLedgerRecord -Sequence $sequence -Label $Label -CommandText $commandText `
            -StartedAt $startedAt -CompletedAt $completedAt -Status 'failed' -ExitCode $nativeExitCode `
            -StdoutPath $StdoutPath -StderrPath $StderrPath -TimeoutSeconds $TimeoutSeconds `
            -Failure $_.Exception.Message
        Write-Host ("[{0:HH:mm:ss}] STEP {1:00} FAIL   {2:N1}s — {3}" -f [DateTimeOffset]::Now, $sequence, ($completedAt - $startedAt).TotalSeconds, $Label) -ForegroundColor Red
        Write-Host "  Review stdout: $StdoutPath" -ForegroundColor Yellow
        Write-Host "  Review stderr: $StderrPath" -ForegroundColor Yellow
        throw
    }
}

function Invoke-QuietBoundedCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string[]] $Command,
        [int] $TimeoutSeconds = 120
    )
    $script:QuietCommandSequence++
    $safeLabel = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $prefix = "quiet-{0:00}-{1}" -f $script:QuietCommandSequence, $safeLabel
    $stdoutPath = Join-Path $logOutput ($prefix + '.stdout.log')
    $stderrPath = Join-Path $logOutput ($prefix + '.stderr.log')
    Invoke-BoundedCommand -Label $Label -Command $Command -StdoutPath $stdoutPath -StderrPath $stderrPath `
        -TimeoutSeconds $TimeoutSeconds -MaximumOutputBytes 8388608
}

try {
    Start-Transcript -LiteralPath (Join-Path $evidenceRoot 'verification-transcript.txt') -Force | Out-Null
    $transcriptStarted = $true

    $requiredSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    $sdkVersionPath = Join-Path $logOutput 'sdk-version.log'
    Invoke-BoundedCommand -Label 'dotnet --version' -Command @('dotnet', '--version') `
        -StdoutPath $sdkVersionPath -StderrPath (Join-Path $logOutput 'sdk-version.stderr.log') -TimeoutSeconds 120
    $actualSdk = (Get-Content $sdkVersionPath -Raw).Trim()
    $msbuildVersionPath = Join-Path $logOutput 'msbuild-version.log'
    Invoke-BoundedCommand -Label 'dotnet msbuild -version' -Command @('dotnet', 'msbuild', '-version', '-nologo') `
        -StdoutPath $msbuildVersionPath -StderrPath (Join-Path $logOutput 'msbuild-version.stderr.log') -TimeoutSeconds 120
    $actualMsbuild = ((Get-Content $msbuildVersionPath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1).Trim()
    Write-Host "Pinned SDK: $requiredSdk; active SDK: $actualSdk; active MSBuild: $actualMsbuild" -ForegroundColor Yellow
    Invoke-BoundedCommand -Label '.NET SDK/MSBuild version policy verification' `
        -Command @('python', 'tools/verify_dotnet_sdk_version.py', '--required', $requiredSdk, '--actual', $actualSdk, '--msbuild', $actualMsbuild) `
        -StdoutPath (Join-Path $logOutput 'sdk-policy.log') `
        -StderrPath (Join-Path $logOutput 'sdk-policy.stderr.log') -TimeoutSeconds 120 -Tee

    $allProjects = @(
        'src/CloudScribe.App/CloudScribe.App.csproj',
        'src/CloudScribe.Application/CloudScribe.Application.csproj',
        'src/CloudScribe.Domain/CloudScribe.Domain.csproj',
        'src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj',
        'src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj',
        'tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj',
        'tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj',
        'tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj',
        'tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj'
    )
    $buildProjects = @(
        'src/CloudScribe.Domain/CloudScribe.Domain.csproj',
        'src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj',
        'src/CloudScribe.Application/CloudScribe.Application.csproj',
        'src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj',
        'src/CloudScribe.App/CloudScribe.App.csproj',
        'tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj',
        'tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj',
        'tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj',
        'tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj'
    )
    $testProjects = @(
        'tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj',
        'tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj',
        'tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj',
        'tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj'
    )

    Remove-GeneratedBuildState
    Invoke-BoundedCommand -Label 'Stage 1 source verification' -Command @('python', 'tools/verify_stage1_source.py') `
        -StdoutPath (Join-Path $logOutput '01-stage1-source.log') -StderrPath (Join-Path $logOutput '01-stage1-source.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'Stage 2 source verification' -Command @('python', 'tools/verify_stage2_source.py') `
        -StdoutPath (Join-Path $logOutput '02-stage2-source.log') -StderrPath (Join-Path $logOutput '02-stage2-source.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'project dependency contract' -Command @('python', 'tools/verify_project_dependencies.py') `
        -StdoutPath (Join-Path $logOutput '03-project-dependencies.log') -StderrPath (Join-Path $logOutput '03-project-dependencies.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'repository SHA-256 manifest preflight' -Command @('python', 'tools/update_sha256_manifest.py', '--check') `
        -StdoutPath (Join-Path $logOutput '03-source-manifest-preflight.log') -StderrPath (Join-Path $logOutput '03-source-manifest-preflight.stderr.log') -TimeoutSeconds 300 -Tee
    $sourceManifestSha256 = (Get-FileHash (Join-Path $repoRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    $repositoryVersion = (Get-Content SESSION_STATE.json -Raw | ConvertFrom-Json).repository_version
    $deliveryRoot = if (-not [string]::IsNullOrWhiteSpace($env:CLOUDSCRIBE_STAGE2_DELIVERY_ROOT)) {
        [IO.Path]::GetFullPath($env:CLOUDSCRIBE_STAGE2_DELIVERY_ROOT)
    }
    else {
        Join-Path (Split-Path $repoRoot -Parent) 'CloudScribe-Windows'
    }
    $deliveryRunRoot = Join-Path $deliveryRoot ("CloudScribe-" + $repositoryVersion + "-" + $stamp)
    $developmentOutput = Join-Path $deliveryRunRoot 'development-candidate'
    $verifiedOutput = Join-Path $deliveryRunRoot 'verified-release'
    $deliveryPointer = Join-Path $deliveryRoot 'LATEST-CLOUDSCRIBE-EXE.txt'
    $powerShellExecutable = (Get-Process -Id $PID).Path
    Write-Host "Runnable output root: $deliveryRoot" -ForegroundColor Yellow
    Invoke-BoundedCommand -Label 'dotnet --info' -Command @('dotnet', '--info') `
        -StdoutPath (Join-Path $logOutput 'dotnet-info.log') -StderrPath (Join-Path $logOutput 'dotnet-info.stderr.log') -TimeoutSeconds 120 -Tee

    for ($index = 0; $index -lt $allProjects.Count; $index++) {
        $project = $allProjects[$index]
        try { Invoke-QuietBoundedCommand -Label 'build-server shutdown' -Command @('dotnet', 'build-server', 'shutdown') } catch { }
        Invoke-BoundedCommand -Label "locked restore for $project" `
            -Command @('dotnet', 'restore', $project, '--locked-mode', '--disable-parallel', '--configfile', 'NuGet.config') `
            -StdoutPath (Join-Path $logOutput "restore-$index.log") `
            -StderrPath (Join-Path $logOutput "restore-$index.stderr.log") -Tee
        try { Invoke-QuietBoundedCommand -Label 'build-server shutdown' -Command @('dotnet', 'build-server', 'shutdown') } catch { }
    }
    foreach ($configuration in @('Debug', 'Release')) {
        for ($index = 0; $index -lt $buildProjects.Count; $index++) {
            $project = $buildProjects[$index]
            Invoke-BoundedCommand -Label "$configuration build for $project" `
                -Command @('dotnet', 'build', $project, '-c', $configuration, '--no-restore', '--disable-build-servers', '-m:1', '-nodeReuse:false', '-p:BuildProjectReferences=false', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false') `
                -StdoutPath (Join-Path $logOutput "build-$configuration-$index.log") `
                -StderrPath (Join-Path $logOutput "build-$configuration-$index.stderr.log") -Tee
            if ($configuration -eq 'Debug' -and $project -eq 'src/CloudScribe.App/CloudScribe.App.csproj') {
                Invoke-BoundedCommand -Label 'publish runnable Debug development candidate' `
                    -Command @($powerShellExecutable, '-NoProfile', '-File', (Join-Path $repoRoot 'scripts/publish-stage2-windows.ps1'), '-OutputDirectory', $developmentOutput, '-Configuration', 'Debug', '-Status', 'development-candidate') `
                    -StdoutPath (Join-Path $logOutput 'publish-development-candidate.log') `
                    -StderrPath (Join-Path $logOutput 'publish-development-candidate.stderr.log') -TimeoutSeconds 300 -Tee
                $script:RunnableExecutable = Join-Path $developmentOutput 'CloudScribe.exe'
                $script:RunnableLogsDirectory = Join-Path $developmentOutput 'logs'
                $script:RunnableStatus = 'development-candidate'
                $env:CLOUDSCRIBE_STAGE2_CANDIDATE_EXE = $script:RunnableExecutable
                $env:CLOUDSCRIBE_STAGE2_CANDIDATE_LOGS = $script:RunnableLogsDirectory
                New-Item -ItemType Directory -Path $deliveryRoot -Force | Out-Null
                Set-Content -LiteralPath $deliveryPointer -Value @(
                    "status=$($script:RunnableStatus)",
                    "exe=$($script:RunnableExecutable)",
                    "runtime_logs=$($script:RunnableLogsDirectory)",
                    "output_directory=$developmentOutput",
                    "repository_version=$repositoryVersion"
                ) -Encoding utf8
                Write-Host "Runnable development executable: $($script:RunnableExecutable)" -ForegroundColor Green
                Write-Host "Runtime logs will appear at: $($script:RunnableLogsDirectory)" -ForegroundColor Yellow
            }
        }
    }
    for ($index = 0; $index -lt $testProjects.Count; $index++) {
        $project = $testProjects[$index]
        $resultDirectory = Join-Path $testOutput ([string]$index)
        New-Item $resultDirectory -ItemType Directory -Force | Out-Null
        Invoke-BoundedCommand -Label "Release tests for $project" `
            -Command @('dotnet', 'test', $project, '-c', 'Release', '--no-build', '--no-restore', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '--results-directory', $resultDirectory, '--logger', 'trx;LogFileName=stage2-tests.trx') `
            -StdoutPath (Join-Path $logOutput "test-$index.log") `
            -StderrPath (Join-Path $logOutput "test-$index.stderr.log") -Tee
    }

    Invoke-BoundedCommand -Label 'dotnet format verification' `
        -Command @('dotnet', 'format', 'CloudScribe.sln', '--verify-no-changes', '--no-restore') `
        -StdoutPath (Join-Path $logOutput 'format.log') -StderrPath (Join-Path $logOutput 'format.stderr.log') -Tee
    for ($index = 0; $index -lt $allProjects.Count; $index++) {
        $project = $allProjects[$index]
        Invoke-BoundedCommand -Label "vulnerability scan for $project" `
            -Command @($powerShellExecutable, '-NoProfile', '-File', (Join-Path $repoRoot 'scripts/invoke-nuget-audit-scan.ps1'), '-Project', $project) `
            -StdoutPath (Join-Path $scanOutput ("$index-vulnerable.json")) `
            -StderrPath (Join-Path $logOutput ("package-$index-vulnerable.stderr.log")) -TimeoutSeconds 300 -MaximumOutputBytes 5242880
        Invoke-BoundedCommand -Label "deprecation scan for $project" `
            -Command @('dotnet', 'package', 'list', '--project', $project, '--deprecated', '--include-transitive', '--no-restore', '--format', 'json', '--output-version', '1') `
            -StdoutPath (Join-Path $scanOutput ("$index-deprecated.json")) `
            -StderrPath (Join-Path $logOutput ("package-$index-deprecated.stderr.log")) -TimeoutSeconds 300 -MaximumOutputBytes 5242880
    }
    Invoke-BoundedCommand -Label 'dependency vulnerability/deprecation scan validation' `
        -Command @('python', 'tools/verify_dotnet_package_scan.py', $scanOutput) `
        -StdoutPath (Join-Path $logOutput 'package-scan-validation.log') `
        -StderrPath (Join-Path $logOutput 'package-scan-validation.stderr.log') -TimeoutSeconds 300 -Tee

    Invoke-BoundedCommand -Label 'Stage 1 Windows smoke script' `
        -Command @($powerShellExecutable, '-NoProfile', '-File', (Join-Path $repoRoot 'scripts/smoke-stage1-windows.ps1')) `
        -StdoutPath (Join-Path $logOutput 'stage1-runtime-smoke.log') `
        -StderrPath (Join-Path $logOutput 'stage1-runtime-smoke.stderr.log') -TimeoutSeconds 180 -Tee
    Invoke-BoundedCommand -Label 'Stage 2 Windows visual capture script' `
        -Command @($powerShellExecutable, '-NoProfile', '-File', (Join-Path $repoRoot 'scripts/capture-stage2-windows.ps1'), $visualOutput) `
        -StdoutPath (Join-Path $logOutput 'stage2-visual-capture.log') `
        -StderrPath (Join-Path $logOutput 'stage2-visual-capture.stderr.log') -TimeoutSeconds 180 -Tee
    Invoke-BoundedCommand -Label 'publish Release verification-pending output' `
        -Command @($powerShellExecutable, '-NoProfile', '-File', (Join-Path $repoRoot 'scripts/publish-stage2-windows.ps1'), '-OutputDirectory', $verifiedOutput, '-Configuration', 'Release', '-Status', 'verification-pending') `
        -StdoutPath (Join-Path $logOutput 'publish-verified-release.log') `
        -StderrPath (Join-Path $logOutput 'publish-verified-release.stderr.log') -TimeoutSeconds 300 -Tee
    $script:RunnableExecutable = Join-Path $verifiedOutput 'CloudScribe.exe'
    $script:RunnableLogsDirectory = Join-Path $verifiedOutput 'logs'
    $script:RunnableStatus = 'verification-pending'
    $env:CLOUDSCRIBE_STAGE2_CANDIDATE_EXE = $script:RunnableExecutable
    $env:CLOUDSCRIBE_STAGE2_CANDIDATE_LOGS = $script:RunnableLogsDirectory

    Remove-GeneratedBuildState
    Invoke-BoundedCommand -Label 'repository SHA-256 manifest check' -Command @('python', 'tools/update_sha256_manifest.py', '--check') `
        -StdoutPath (Join-Path $logOutput 'sha256-manifest.log') -StderrPath (Join-Path $logOutput 'sha256-manifest.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'Stage 1 source re-verification' -Command @('python', 'tools/verify_stage1_source.py') `
        -StdoutPath (Join-Path $logOutput 'stage1-source-final.log') -StderrPath (Join-Path $logOutput 'stage1-source-final.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'Stage 2 source re-verification' -Command @('python', 'tools/verify_stage2_source.py') `
        -StdoutPath (Join-Path $logOutput 'stage2-source-final.log') -StderrPath (Join-Path $logOutput 'stage2-source-final.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'repository governance verification' -Command @('python', 'tools/verify_repository.py') `
        -StdoutPath (Join-Path $logOutput 'repository-governance.log') -StderrPath (Join-Path $logOutput 'repository-governance.stderr.log') -TimeoutSeconds 300 -Tee
    Invoke-BoundedCommand -Label 'complete Python regression inventory' -Command @('python', 'tools/run_python_regression_shards.py', '--all') `
        -StdoutPath (Join-Path $logOutput 'python-regression-inventory.log') -StderrPath (Join-Path $logOutput 'python-regression-inventory.stderr.log') -TimeoutSeconds 900 -Tee

    Invoke-BoundedCommand -Label 'Stage 2 exact bounded evidence inventory validation' `
        -Command @('python', 'tools/verify_stage2_evidence_inventory.py', $evidenceRoot) `
        -StdoutPath (Join-Path $logOutput 'stage2-evidence-inventory.log') `
        -StderrPath (Join-Path $logOutput 'stage2-evidence-inventory.stderr.log') -TimeoutSeconds 300 -Tee
    $finalSourceManifestSha256 = (Get-FileHash (Join-Path $repoRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    $finalRepositoryVersion = (Get-Content SESSION_STATE.json -Raw | ConvertFrom-Json).repository_version
    if ($finalSourceManifestSha256 -ne $sourceManifestSha256) { throw 'Source manifest changed during Stage 2 verification.' }
    if ($finalRepositoryVersion -ne $repositoryVersion) { throw 'Repository version changed during Stage 2 verification.' }
    $script:RunnableStatus = 'verified-stage2'
    Set-Content -LiteralPath (Join-Path $verifiedOutput 'BUILD-STATUS.txt') -Value @(
        'CloudScribe Pro Windows Stage 2 verified output',
        "repository_version=$repositoryVersion",
        'configuration=Release',
        "status=$($script:RunnableStatus)",
        "verified_at_utc=$([DateTimeOffset]::UtcNow.ToString('o'))",
        'Application runtime logs are written to the logs folder beside CloudScribe.exe.',
        'Build and verification logs are mirrored to logs\build when the launcher exits.'
    ) -Encoding utf8
    New-Item -ItemType Directory -Path $deliveryRoot -Force | Out-Null
    Set-Content -LiteralPath $deliveryPointer -Value @(
        "status=$($script:RunnableStatus)",
        "exe=$($script:RunnableExecutable)",
        "runtime_logs=$($script:RunnableLogsDirectory)",
        "output_directory=$verifiedOutput",
        "repository_version=$repositoryVersion"
    ) -Encoding utf8
    $packageScanCount = 18
    $screenshotCount = 17
    $testResultCount = 4

    $summary = [ordered]@{
        schema = 'cloudscribe-stage2-verification-summary-1.0'
        completed_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'passed'
        platform = 'Windows'
        dotnet_sdk = $actualSdk
        repository_version = $repositoryVersion
        source_manifest_sha256 = $sourceManifestSha256
        evidence_retained = $true
        package_scan_files = $packageScanCount
        screenshot_files = $screenshotCount
        test_result_files = $testResultCount
        command_count = $script:CommandSequence
        runnable_status = $script:RunnableStatus
        runnable_executable = $script:RunnableExecutable
        runtime_logs_directory = $script:RunnableLogsDirectory
        latest_executable_pointer = $deliveryPointer
        command_ledger = $commandLedgerPath
        transcript = (Join-Path $evidenceRoot 'verification-transcript.txt')
        started_at_utc = $verificationStartedAt.ToString('o')
        duration_seconds = [Math]::Round(([DateTimeOffset]::UtcNow - $verificationStartedAt).TotalSeconds, 3)
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $evidenceRoot 'verification-summary.json')
    Write-Host ''
    Write-Host 'STAGE 2 VERIFICATION PASSED' -ForegroundColor Green
    Write-Host "Stage 2 verification evidence retained at: $evidenceRoot" -ForegroundColor Yellow
}
catch {
    $completedAt = [DateTimeOffset]::UtcNow
    $failedSummary = [ordered]@{
        schema = 'cloudscribe-stage2-verification-summary-1.0'
        completed_at_utc = $completedAt.ToString('o')
        started_at_utc = $verificationStartedAt.ToString('o')
        duration_seconds = [Math]::Round(($completedAt - $verificationStartedAt).TotalSeconds, 3)
        status = 'failed'
        platform = 'Windows'
        repository_version = if (Test-Path -LiteralPath (Join-Path $repoRoot 'SESSION_STATE.json')) { (Get-Content (Join-Path $repoRoot 'SESSION_STATE.json') -Raw | ConvertFrom-Json).repository_version } else { $null }
        failed_step = $script:CommandSequence
        error = $_.Exception.Message
        command_ledger = $commandLedgerPath
        transcript = (Join-Path $evidenceRoot 'verification-transcript.txt')
        command_count = $script:CommandSequence
        runnable_status = $script:RunnableStatus
        runnable_executable = $script:RunnableExecutable
        runtime_logs_directory = $script:RunnableLogsDirectory
        latest_executable_pointer = if (-not [string]::IsNullOrWhiteSpace($deliveryPointer) -and (Test-Path -LiteralPath $deliveryPointer)) { $deliveryPointer } else { $null }
        evidence_retained = $true
    }
    $failedSummary | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $evidenceRoot 'verification-summary.json')
    Write-Host ''
    Write-Host 'STAGE 2 VERIFICATION FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Evidence and all completed command logs were retained at: $evidenceRoot" -ForegroundColor Yellow
    Write-Host "Command ledger: $commandLedgerPath" -ForegroundColor Yellow
    if (-not [string]::IsNullOrWhiteSpace($script:RunnableExecutable) -and (Test-Path -LiteralPath $script:RunnableExecutable -PathType Leaf)) {
        Write-Host "A runnable development executable was retained at: $($script:RunnableExecutable)" -ForegroundColor Green
        Write-Host "Its runtime logs folder is: $($script:RunnableLogsDirectory)" -ForegroundColor Yellow
    }
    throw
}
finally {
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
    Remove-GeneratedBuildState
}
