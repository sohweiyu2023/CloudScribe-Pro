param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location -LiteralPath $SourceRoot
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action)
    Write-Host "== $Label =="
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) {
        throw "$Label hash mismatch: $actual"
    }
    Write-Host ('Verified {0}: {1}' -f $Label, $actual)
}

$sdk = (& dotnet --version).Trim()
if ($sdk -ne '10.0.302') {
    throw "Expected .NET SDK 10.0.302, got $sdk."
}

Assert-Sha256 -Path (Join-Path $SourceRoot 'scripts/invoke-nuget-audit-scan.ps1') `
    -Expected 'de5981b2ef579c6b85261cd5ef8543cf937b79243688804777987c2274e41841' `
    -Label 'final Windows NuGet audit wrapper'
Assert-Sha256 -Path (Join-Path $SourceRoot 'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs') `
    -Expected 'f92c0f76d5e278efe91e071056bb853117100a6b0702463d4fbd35ca18fe1819' `
    -Label 'final architecture regression contract'
Assert-Sha256 -Path (Join-Path $SourceRoot 'tests/CloudScribe.Infrastructure.Tests/StartupAndDiagnosticsResilienceTests.cs') `
    -Expected 'd0d6a3d8e2a88aa09ecb1fb6a00943d71a6b4c92d0d36e37e94c0b5e83edb764' `
    -Label 'final diagnostics rotation regression test'
Assert-Sha256 -Path (Join-Path $SourceRoot 'scripts/smoke-stage1-windows.ps1') `
    -Expected '7b1a564dde8677bf20809c5a582a7491caa76c00efc8e80088fcad6f34e892df' `
    -Label 'final native Windows smoke script'

$visualCaptureTarget = Join-Path $SourceRoot 'src/CloudScribe.App/MainWindow.VisualCapture.cs'
$visualCaptureOverlay = Join-Path $env:GITHUB_WORKSPACE '.cloudscribe-ci/final-overlay/MainWindow.VisualCapture.cs'
$visualCaptureExpected = '43d3cb6e9b1af5cfe35d294cf32a7598736ca1dcd67d7697bc6d01fe0d2ff838'
if (-not (Test-Path -LiteralPath $visualCaptureOverlay -PathType Leaf)) {
    throw "Visual capture overlay is missing: $visualCaptureOverlay"
}
$visualText = [IO.File]::ReadAllText($visualCaptureOverlay).Replace("`r`n", "`n").Replace("`r", "`n")
[IO.File]::WriteAllText($visualCaptureTarget, $visualText, $utf8NoBom)
Assert-Sha256 -Path $visualCaptureTarget -Expected $visualCaptureExpected -Label 'final Stage 2 visual capture source'
$visualContract = [IO.File]::ReadAllText($visualCaptureTarget)
foreach ($required in @(
    'captureRoot.Measure(targetSize)',
    'captureRoot.Arrange(new Rect(0, 0, targetSize.Width, targetSize.Height))',
    'bitmap.Render(captureRoot)',
    'PixelSize capturedSize = CaptureWindow(path, captureCase.Width, captureCase.Height)')) {
    if (-not $visualContract.Contains($required)) {
        throw "Visual capture sizing contract is missing: $required"
    }
}
if ($visualContract.Contains('bitmap.Render(this)')) {
    throw 'Visual capture must not render the screen-clamped top-level Window.'
}

Invoke-Checked 'Generate deterministic SHA256SUMS.txt' { python tools/update_sha256_manifest.py }
Invoke-Checked 'Verify deterministic SHA256SUMS.txt' { python tools/update_sha256_manifest.py --check }

$projects = @(
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
$buildOrder = @(
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

foreach ($project in $projects) {
    & dotnet build-server shutdown | Out-Null
    Invoke-Checked "Locked restore $project" {
        dotnet restore $project --locked-mode --disable-parallel --configfile NuGet.config
    }
}
foreach ($configuration in @('Debug', 'Release')) {
    foreach ($project in $buildOrder) {
        Invoke-Checked "$configuration build $project" {
            dotnet build $project -c $configuration --no-restore --disable-build-servers `
                -m:1 -nodeReuse:false -p:BuildProjectReferences=false `
                -p:BuildInParallel=false -p:UseSharedCompilation=false
        }
    }
}

$resultRoot = Join-Path $env:RUNNER_TEMP 'test-results'
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
for ($index = 0; $index -lt $testProjects.Count; $index++) {
    $resultDirectory = Join-Path $resultRoot $index
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    Invoke-Checked "Test $($testProjects[$index])" {
        dotnet test $testProjects[$index] -c Release --no-build --no-restore `
            -m:1 -nodeReuse:false -p:UseSharedCompilation=false `
            --results-directory $resultDirectory --logger 'trx;LogFileName=stage2-tests.trx'
    }
}

$total = 0
$failed = 0
$skipped = 0
$trxCount = 0
foreach ($trx in Get-ChildItem -LiteralPath $resultRoot -Filter '*.trx' -File -Recurse) {
    $trxCount++
    [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw
    $counters = $xml.SelectSingleNode("//*[local-name()='Counters']")
    if (-not $counters) { throw "Missing counters in $($trx.FullName)." }
    $totalValue = $counters.GetAttribute('total')
    $failedValue = $counters.GetAttribute('failed')
    $skippedValue = $counters.GetAttribute('skipped')
    if ([string]::IsNullOrWhiteSpace($totalValue) -or [string]::IsNullOrWhiteSpace($failedValue)) {
        throw "Missing required counter attributes in $($trx.FullName)."
    }
    $total += [int]$totalValue
    $failed += [int]$failedValue
    if (-not [string]::IsNullOrWhiteSpace($skippedValue)) { $skipped += [int]$skippedValue }
}
if ($trxCount -ne 4 -or $total -ne 146 -or $failed -ne 0 -or $skipped -ne 0) {
    throw "Unexpected test inventory: trx=$trxCount total=$total failed=$failed skipped=$skipped; expected 4/146/0/0."
}
Write-Host 'PASS: 146/146 .NET tests.'

Invoke-Checked 'dotnet format --verify-no-changes' {
    dotnet format CloudScribe.sln --verify-no-changes --no-restore
}

$scanRoot = Join-Path $env:RUNNER_TEMP 'package-scans'
$scanLogRoot = Join-Path $env:RUNNER_TEMP 'package-scan-logs'
New-Item -ItemType Directory -Path $scanRoot, $scanLogRoot -Force | Out-Null
for ($index = 0; $index -lt $projects.Count; $index++) {
    $vulnerableJson = Join-Path $scanRoot "$index-vulnerable.json"
    $vulnerableError = Join-Path $scanLogRoot "$index-vulnerable.stderr.log"
    Write-Host "== Vulnerability scan $($projects[$index]) =="
    & pwsh -NoProfile -File scripts/invoke-nuget-audit-scan.ps1 `
        -Project $projects[$index] 1> $vulnerableJson 2> $vulnerableError
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $vulnerableError) { Get-Content -LiteralPath $vulnerableError }
        throw "NuGet vulnerability audit failed: $($projects[$index])"
    }
    Write-Host "== Deprecation scan $($projects[$index]) =="
    & dotnet package list --project $projects[$index] --deprecated --include-transitive `
        --no-restore --format json --output-version 1 |
        Set-Content -LiteralPath (Join-Path $scanRoot "$index-deprecated.json") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "NuGet deprecation scan failed: $($projects[$index])" }
}
Invoke-Checked 'Validate paired package scan JSON reports' {
    python tools/verify_dotnet_package_scan.py $scanRoot
}

$publishRoot = Join-Path $env:RUNNER_TEMP 'CloudScribe-publish'
Invoke-Checked 'Publish Windows candidate' {
    pwsh -NoProfile -File scripts/publish-stage2-windows.ps1 `
        -OutputDirectory $publishRoot -Configuration Release -Status verification-pending
}
Invoke-Checked 'Native Windows launch + secondary activation smoke' {
    pwsh -NoProfile -File scripts/smoke-stage1-windows.ps1
}
$screenRoot = Join-Path $env:RUNNER_TEMP 'stage2-screenshots'
Invoke-Checked 'Native 17-case Stage 2 visual capture' {
    pwsh -NoProfile -File scripts/capture-stage2-windows.ps1 $screenRoot
}
Invoke-Checked 'Final source manifest re-check' {
    python tools/update_sha256_manifest.py --check
}

Write-Host 'CLOUDSCRIBE_STAGE2_NATIVE_WINDOWS_CERTIFICATION=PASS'
