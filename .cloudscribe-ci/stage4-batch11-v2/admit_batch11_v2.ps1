$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$baseHead = 'e21f1a055e22f99bd6a3d88d6e2802b6d0b6d4da'
$targetBranch = 'cloudscribe-0.5.0-stage4'
$carrierBranch = 'cloudscribe-stage4-batch11-v2-dev'
$transformBlob = '6aea99c0fba5fcb92ee79ce2ba238f1c80b55e9b'
$expectedFingerprint = 'f8311ad3beefcd6b6182c176d2b3c704ff5513c54f5ca98e4b26a754292f4dc3'
$expectedChanged = 8
$expectedVerifier = 81
$expectedRegressions = 153
$expectedDotnetTests = 260
$testRoot = Join-Path $env:RUNNER_TEMP 'stage4-batch11-v2-tests'
$packageRoot = Join-Path $env:RUNNER_TEMP 'stage4-batch11-v2-package'
$evidenceRoot = Join-Path $env:RUNNER_TEMP 'stage4-batch11-v2-evidence'
$status = 'failure'
$exitCode = 1
$testedHead = ''
$sourceZipSha = ''

function Need([bool]$ok, [string]$message) {
    if (-not $ok) { throw $message }
}

function Get-StagedPaths {
    return @(git diff --cached --name-only | Where-Object { $_ -and $_.Trim() } | Sort-Object -Unique)
}

function Get-IndexFingerprint([string[]]$paths) {
    $rows = @()
    foreach ($path in $paths) {
        $entry = (git ls-files -s -- $path | Select-Object -First 1)
        Need (-not [string]::IsNullOrWhiteSpace($entry)) "Missing staged Git index entry for $path"
        $parts = $entry -split '\s+'
        Need ($parts.Count -ge 2) "Unable to parse staged Git index entry for $path"
        $rows += "$path`t$($parts[1])"
    }
    $identity = ($rows -join "`n") + "`n"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($identity)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-Locator([string]$value) {
    $locator = Join-Path $env:GITHUB_WORKSPACE 'carrier/.cloudscribe-ci/stage4-batch11-v2/admission-v2-status.txt'
    [IO.Directory]::CreateDirectory((Split-Path $locator -Parent)) | Out-Null
    $body = @(
        "RUN_ID=$env:GITHUB_RUN_ID"
        "RUN_ATTEMPT=$env:GITHUB_RUN_ATTEMPT"
        "STATUS=$value"
        "SOURCE_BASE=$baseHead"
        "TRANSFORM_BLOB=$transformBlob"
        "EXPECTED_INDEX_FINGERPRINT_SHA256=$expectedFingerprint"
        "EXPECTED_CHANGED_FILES=$expectedChanged"
        "EXPECTED_VERIFIER_TESTS=$expectedVerifier"
        "EXPECTED_DOTNET_TESTS=$expectedDotnetTests"
        "TESTED_HEAD=$testedHead"
        "SOURCE_ZIP_SHA256=$sourceZipSha"
    ) -join "`n"
    [IO.File]::WriteAllText($locator, $body + "`n", [Text.UTF8Encoding]::new($false))
    git -C carrier config user.name cloudscribe-ci
    git -C carrier config user.email actions@github.com
    git -C carrier add .cloudscribe-ci/stage4-batch11-v2/admission-v2-status.txt
    git -C carrier commit -m "ci: record Batch 11 v2 $value run $env:GITHUB_RUN_ID"
    if ($LASTEXITCODE -eq 0) {
        git -C carrier push origin "HEAD:refs/heads/$carrierBranch"
        Need ($LASTEXITCODE -eq 0) 'Unable to publish Batch 11 v2 admission locator.'
    }
}

try {
    Write-Locator 'in_progress'

    $transformPath = Join-Path $env:GITHUB_WORKSPACE 'carrier/.cloudscribe-ci/stage4-batch11-v2/apply_batch11.py'
    Need (Test-Path $transformPath -PathType Leaf) 'Batch 11 v2 transform is missing.'
    $actualTransformBlob = (git -C carrier hash-object .cloudscribe-ci/stage4-batch11-v2/apply_batch11.py).Trim()
    Need ($actualTransformBlob -eq $transformBlob) "Batch 11 v2 transform blob mismatch: $actualTransformBlob"

    Set-Location (Join-Path $env:GITHUB_WORKSPACE 'source')
    Need ((git rev-parse HEAD).Trim() -eq $baseHead) 'Stage 4 target moved before Batch 11 v2 admission.'
    Need ((dotnet --version).Trim() -eq '10.0.400') 'Wrong .NET SDK; expected exact 10.0.400.'

    python $transformPath .
    Need ($LASTEXITCODE -eq 0) 'Batch 11 v2 deterministic source transform failed.'
    git add -A
    Need ($LASTEXITCODE -eq 0) 'Unable to stage Batch 11 v2 candidate.'

    $staged = Get-StagedPaths
    Need ($staged.Count -eq $expectedChanged) "Expected $expectedChanged staged paths; found $($staged.Count): $($staged -join ', ')"
    Need (@($staged | Where-Object { $_ -match 'packages\.lock\.json$' -or $_ -match '(^|/)(bin|obj|TestResults|__pycache__)(/|$)' }).Count -eq 0) 'Batch 11 v2 contains forbidden generated or lockfile changes.'
    git diff --cached --check
    Need ($LASTEXITCODE -eq 0) 'Batch 11 v2 staged whitespace check failed.'

    $fingerprint = Get-IndexFingerprint $staged
    Write-Host "CLOUDSCRIBE_STAGE4_BATCH11_V2_INDEX_FINGERPRINT_SHA256=$fingerprint"
    Need ($fingerprint -eq $expectedFingerprint) "Batch 11 v2 canonical index fingerprint mismatch: $fingerprint"

    @'
import pathlib
import subprocess
paths = subprocess.check_output(["git", "diff", "--cached", "--name-only"], text=True, encoding="utf-8").splitlines()
for rel in paths:
    data = subprocess.check_output(["git", "show", f":{rel}"])
    path = pathlib.Path(rel)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
'@ | python -
    Need ($LASTEXITCODE -eq 0) 'Unable to rematerialize Batch 11 v2 working-tree bytes from the verified Git index.'
    git diff --exit-code
    Need ($LASTEXITCODE -eq 0) 'Batch 11 v2 working tree differs from the verified staged source.'

    foreach ($command in @(
        'python tools/update_sha256_manifest.py --check',
        'python tools/verify_repository.py',
        'python tools/verify_project_dependencies.py',
        'python tools/verify_stage1_source.py',
        'python tools/verify_stage2_source.py',
        'python tools/verify_stage3_source.py',
        'python tools/verify_stage4_source.py')) {
        Invoke-Expression $command
        if ($LASTEXITCODE -ne 0) { throw "Gate failed: $command" }
    }

    $verifier = @(& python -B tools/run_verifier_self_tests.py 2>&1)
    $verifierCode = $LASTEXITCODE
    $verifier | ForEach-Object { Write-Host $_ }
    Need ($verifierCode -eq 0) 'Verifier self-tests failed.'
    Need (($verifier -join "`n") -match "PASS: $expectedVerifier/$expectedVerifier isolated auxiliary Python verifier self-tests") 'Exact 81/81 verifier self-test count missing.'

    $regressions = @(& python -B tools/run_python_regression_shards.py --all 2>&1)
    $regressionCode = $LASTEXITCODE
    $regressions | ForEach-Object { Write-Host $_ }
    Need ($regressionCode -eq 0) 'Deterministic regression suite failed.'
    Need (($regressions -join "`n") -match "$expectedRegressions/$expectedRegressions") 'Exact 153/153 regression count missing.'

    dotnet restore CloudScribe.sln --locked-mode --disable-parallel --configfile NuGet.config
    Need ($LASTEXITCODE -eq 0) 'Locked restore failed.'
    dotnet format CloudScribe.sln --no-restore --verify-no-changes --verbosity minimal
    Need ($LASTEXITCODE -eq 0) 'Format gate failed.'
    dotnet build CloudScribe.sln -c Release --no-restore --disable-build-servers -m:1 -nodeReuse:false -p:BuildInParallel=false -p:UseSharedCompilation=false
    Need ($LASTEXITCODE -eq 0) 'Release compiler/analyzer gate failed.'

    New-Item -ItemType Directory -Force $testRoot | Out-Null
    $projects = @(
        'tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj',
        'tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj',
        'tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj',
        'tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj')
    for ($i = 0; $i -lt $projects.Count; $i++) {
        $dir = Join-Path $testRoot $i
        New-Item -ItemType Directory -Force $dir | Out-Null
        dotnet test $projects[$i] -c Release --no-build --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --results-directory $dir --logger 'trx;LogFileName=batch11-v2.trx'
        Need ($LASTEXITCODE -eq 0) "Tests failed: $($projects[$i])"
    }

    $trx = @(Get-ChildItem $testRoot -Filter '*.trx' -File -Recurse)
    Need ($trx.Count -eq 4) "Expected 4 TRX files; found $($trx.Count)."
    $total = 0
    $passed = 0
    $failed = 0
    $skipped = 0
    foreach ($file in $trx) {
        [xml]$xml = Get-Content $file.FullName -Raw
        $counters = $xml.TestRun.ResultSummary.Counters
        $total += [int]$counters.total
        $passed += [int]$counters.passed
        $failed += [int]$counters.failed
        $skipped += [int]$counters.notExecuted
    }
    Write-Host "CLOUDSCRIBE_STAGE4_BATCH11_V2_DOTNET_TESTS total=$total passed=$passed failed=$failed skipped=$skipped"
    Need ($total -eq $expectedDotnetTests -and $passed -eq $expectedDotnetTests -and $failed -eq 0 -and $skipped -eq 0) 'Exact 260 compiled-test gate failed.'

    python tools/update_sha256_manifest.py --check
    Need ($LASTEXITCODE -eq 0) 'Manifest drifted after tests.'
    python tools/verify_stage4_source.py
    Need ($LASTEXITCODE -eq 0) 'Stage 4 verifier failed after tests.'
    git diff --cached --check
    Need ($LASTEXITCODE -eq 0) 'Staged source drifted after tests.'
    git diff --exit-code
    Need ($LASTEXITCODE -eq 0) 'Tracked working tree drifted after tests.'

    dotnet build-server shutdown | Out-Null
    git clean -fdx
    Need ($LASTEXITCODE -eq 0) 'Generated-state cleanup failed.'
    python tools/update_sha256_manifest.py --check
    Need ($LASTEXITCODE -eq 0) 'Tracked source changed during cleanup.'

    $stagedAfter = Get-StagedPaths
    Need ($stagedAfter.Count -eq $expectedChanged) "Expected $expectedChanged staged paths after tests; found $($stagedAfter.Count)."
    $fingerprintAfter = Get-IndexFingerprint $stagedAfter
    Need ($fingerprintAfter -eq $expectedFingerprint) "Batch 11 v2 index fingerprint drifted after tests: $fingerprintAfter"

    $remote = (git ls-remote --heads origin "refs/heads/$targetBranch" | Select-Object -First 1)
    $remoteHead = ($remote -split '\s+')[0].Trim()
    Need ($remoteHead -eq $baseHead) "Stage 4 target moved before Batch 11 v2 publication: $remoteHead"

    git config user.name cloudscribe-ci
    git config user.email actions@github.com
    git commit -m 'feat: add Stage 4 pricing plan contracts batch 11'
    Need ($LASTEXITCODE -eq 0) 'Batch 11 v2 commit failed.'
    $testedHead = (git rev-parse HEAD).Trim()
    git push origin "${testedHead}:refs/heads/$targetBranch"
    Need ($LASTEXITCODE -eq 0) 'Unable to publish exact tested Batch 11 v2 source.'
    Write-Host "CLOUDSCRIBE_STAGE4_BATCH11_V2_TESTED_HEAD=$testedHead"

    New-Item -ItemType Directory -Force $packageRoot | Out-Null
    python tools/create_source_archive.py --output-directory $packageRoot
    Need ($LASTEXITCODE -eq 0) 'Deterministic Batch 11 v2 source archive creation failed.'
    $sourceZip = @(Get-ChildItem $packageRoot -Filter '*.zip' -File)
    Need ($sourceZip.Count -eq 1) "Expected exactly one deterministic Batch 11 source ZIP; found $($sourceZip.Count)."
    $sourceZipSha = (Get-FileHash $sourceZip[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "CLOUDSCRIBE_STAGE4_BATCH11_V2_SOURCE_ZIP_SHA256=$sourceZipSha"

    New-Item -ItemType Directory -Force $evidenceRoot | Out-Null
    $evidence = @(
        "RUN_ID=$env:GITHUB_RUN_ID"
        "SOURCE_BASE=$baseHead"
        "TESTED_HEAD=$testedHead"
        "INDEX_FINGERPRINT_SHA256=$expectedFingerprint"
        "VERIFIER_TESTS=$expectedVerifier/$expectedVerifier"
        "REGRESSIONS=$expectedRegressions/$expectedRegressions"
        "DOTNET_TESTS=$expectedDotnetTests/$expectedDotnetTests"
        "SOURCE_ZIP_SHA256=$sourceZipSha"
    ) -join "`n"
    [IO.File]::WriteAllText((Join-Path $evidenceRoot 'BATCH11-V2-EVIDENCE.txt'), $evidence + "`n", [Text.UTF8Encoding]::new($false))

    $status = 'success'
    $exitCode = 0
}
catch {
    Write-Host "BATCH11_V2_FAILURE: $($_.Exception.Message)" -ForegroundColor Red
    $status = 'failure'
    $exitCode = 1
}
finally {
    try {
        Set-Location $env:GITHUB_WORKSPACE
        git -C carrier pull --ff-only origin $carrierBranch | Out-Null
        Write-Locator $status
    }
    catch {
        Write-Host "BATCH11_V2_LOCATOR_FAILURE: $($_.Exception.Message)" -ForegroundColor Red
        $exitCode = 1
    }
}

exit $exitCode
