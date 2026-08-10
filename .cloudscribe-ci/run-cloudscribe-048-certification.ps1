param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'
Set-Location -LiteralPath $SourceRoot

function Invoke-Checked {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "== $Label =="
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Assert-Sha256 {
    param([string]$Path, [string]$Expected, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "$Label hash mismatch: $actual" }
    Write-Host "Verified ${Label}: $actual"
}

$sdk = (& dotnet --version).Trim()
if ($sdk -ne '10.0.302') { throw "Expected .NET SDK 10.0.302, got $sdk." }

# Overlay the exact user-reported editor-focus repair and its current regression coverage.
# The carrier is stored on an isolated helper branch and pinned here by immutable commit SHA.
$carrierCommit = '54e1882557b13e8ea548c568995c94807dce23f8'
$carrierRoot = "https://raw.githubusercontent.com/sohweiyu2023/CloudScribe-Pro/$carrierCommit/.cloudscribe-ci/focus-fix-carrier"
$carrierB64 = Join-Path $env:RUNNER_TEMP 'cloudscribe-focus-fix.b64'
$carrierXz = Join-Path $env:RUNNER_TEMP 'cloudscribe-focus-fix.tar.xz'
Remove-Item $carrierB64, $carrierXz -Force -ErrorAction SilentlyContinue
foreach ($part in @('000.b64','001.b64','002.b64','003.b64')) {
    $partPath = Join-Path $env:RUNNER_TEMP $part
    Invoke-WebRequest -Uri "$carrierRoot/$part" -OutFile $partPath
    [IO.File]::AppendAllText($carrierB64, ([IO.File]::ReadAllText($partPath)).Trim())
}
[IO.File]::WriteAllBytes($carrierXz, [Convert]::FromBase64String([IO.File]::ReadAllText($carrierB64)))
if ((Get-Item $carrierXz).Length -ne 42108) { throw 'Focus-fix carrier length mismatch.' }
Assert-Sha256 $carrierXz 'd182d7686fe80b863d8986c2170464efc22aaff4ec584167d804d3a82cf620d6' 'focus-fix carrier'
& tar.exe -xJf $carrierXz -C $SourceRoot
if ($LASTEXITCODE -ne 0) { throw "Focus-fix carrier extraction failed: $LASTEXITCODE" }

# The first native certification exposed that the package version used by this source tree
# does not expose IFocusManager.ClearFocus(). For deterministic visual-capture setup, clear
# focus through the supported Focus(null, ...) path instead. This runs only in the capture
# harness and is guarded by the exact preimage hash plus an exactly-one replacement check.
$visualCapturePath = Join-Path $SourceRoot 'src/CloudScribe.App/MainWindow.VisualCapture.cs'
Assert-Sha256 $visualCapturePath '19d0add468fd32db993e4cd93baaf832c00d4050e330b36a502cbf98ffbf2fe3' 'MainWindow.VisualCapture.cs preimage'
$visualCaptureText = [IO.File]::ReadAllText($visualCapturePath).Replace("`r`n", "`n").Replace("`r", "`n")
$clearFocusPattern = '(?m)^(?<indent>\s*)(?<prefix>(?:this\.)?FocusManager)\?\.ClearFocus\(\);\s*$'
$clearFocusMatches = [regex]::Matches($visualCaptureText, $clearFocusPattern)
if ($clearFocusMatches.Count -ne 1) {
    throw "Expected exactly one FocusManager?.ClearFocus() capture-harness call, found $($clearFocusMatches.Count)."
}
$visualCaptureText = [regex]::Replace(
    $visualCaptureText,
    $clearFocusPattern,
    '${indent}${prefix}?.Focus(null!, Avalonia.Input.NavigationMethod.Unspecified, Avalonia.Input.KeyModifiers.None);',
    1)
if ($visualCaptureText.Contains('ClearFocus()', [StringComparison]::Ordinal)) {
    throw 'ClearFocus() remains in MainWindow.VisualCapture.cs after deterministic compatibility repair.'
}
[IO.File]::WriteAllText($visualCapturePath, $visualCaptureText, [Text.UTF8Encoding]::new($false))
$visualCapturePostHash = (Get-FileHash -LiteralPath $visualCapturePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified MainWindow.VisualCapture.cs deterministic postimage: $visualCapturePostHash"

# Preserve all architecture assertions while keeping the analyzer's method-size contract.
# The focus regression added one assertion to a method already at its 40-statement ceiling.
# Fold the two equivalent forbidden-FontSize checks into one regex assertion, and update the
# focus-harness expectation to the stable Focus(null, ...) API used above.
$adaptiveShellPath = Join-Path $SourceRoot 'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs'
Assert-Sha256 $adaptiveShellPath 'f52c3606a4c093b4053b1faf265f0bd6c267eabd8004c83e0ff6011488e4b26e' 'AdaptiveShellTests.cs preimage'
$adaptiveShellText = [IO.File]::ReadAllText($adaptiveShellPath).Replace("`r`n", "`n").Replace("`r", "`n")
$fontSizeOld = '        Assert.DoesNotContain("FontSize=\"10\"", window, StringComparison.Ordinal);' + "`n" + '        Assert.DoesNotContain("FontSize=\"30\"", window, StringComparison.Ordinal);'
$fontSizeNew = '        Assert.DoesNotMatch("FontSize=\"(?:10|30)\"", window);'
if (-not $adaptiveShellText.Contains($fontSizeOld, [StringComparison]::Ordinal)) {
    throw 'AdaptiveShellTests.cs forbidden-FontSize assertion block was not found exactly once.'
}
$adaptiveShellText = $adaptiveShellText.Replace($fontSizeOld, $fontSizeNew, [StringComparison]::Ordinal)
$focusAssertionOld = '        Assert.Contains("FocusManager?.ClearFocus()", capture, StringComparison.Ordinal);'
$focusAssertionNew = '        Assert.Contains("FocusManager?.Focus(null!, Avalonia.Input.NavigationMethod.Unspecified, Avalonia.Input.KeyModifiers.None)", capture, StringComparison.Ordinal);'
if (-not $adaptiveShellText.Contains($focusAssertionOld, [StringComparison]::Ordinal)) {
    throw 'AdaptiveShellTests.cs legacy ClearFocus assertion was not found.'
}
$adaptiveShellText = $adaptiveShellText.Replace($focusAssertionOld, $focusAssertionNew, [StringComparison]::Ordinal)
if ($adaptiveShellText.Contains('FocusManager?.ClearFocus()', [StringComparison]::Ordinal)) {
    throw 'Legacy ClearFocus architecture expectation remains after deterministic repair.'
}
[IO.File]::WriteAllText($adaptiveShellPath, $adaptiveShellText, [Text.UTF8Encoding]::new($false))
$adaptiveShellPostHash = (Get-FileHash -LiteralPath $adaptiveShellPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified AdaptiveShellTests.cs deterministic postimage: $adaptiveShellPostHash"

# Four diagnostics-writer integration tests passed individually and as a class, but failed only
# when unrelated xUnit test collections saturated the hosted Windows runner. Keep every assertion
# intact and place only this filesystem/background-writer integration class in a collection that
# xUnit will not run concurrently with other collections. This removes scheduler-dependent timing
# without reducing behavioral coverage or increasing product timeouts.
$diagnosticsTestsPath = Join-Path $SourceRoot 'tests/CloudScribe.Infrastructure.Tests/StartupAndDiagnosticsResilienceTests.cs'
Assert-Sha256 $diagnosticsTestsPath 'd0d6a3d8e2a88aa09ecb1fb6a00943d71a6b4c92d0d36e37e94c0b5e83edb764' 'StartupAndDiagnosticsResilienceTests.cs preimage'
$diagnosticsTestsText = [IO.File]::ReadAllText($diagnosticsTestsPath).Replace("`r`n", "`n").Replace("`r", "`n")
$classAnchor = 'public sealed class StartupAndDiagnosticsResilienceTests'
if (($diagnosticsTestsText.Split($classAnchor).Length - 1) -ne 1) {
    throw 'Expected exactly one StartupAndDiagnosticsResilienceTests declaration.'
}
$diagnosticsTestsText = $diagnosticsTestsText.Replace(
    $classAnchor,
    '[Collection("Diagnostic writer integration")]' + "`n" + $classAnchor,
    [StringComparison]::Ordinal)
$collectionDefinition = @'

[CollectionDefinition("Diagnostic writer integration", DisableParallelization = true)]
public sealed class DiagnosticWriterIntegrationCollection
{
}
'@
$diagnosticsTestsText = $diagnosticsTestsText.TrimEnd() + $collectionDefinition + "`n"
[IO.File]::WriteAllText($diagnosticsTestsPath, $diagnosticsTestsText, [Text.UTF8Encoding]::new($false))
$diagnosticsTestsPostHash = (Get-FileHash -LiteralPath $diagnosticsTestsPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified StartupAndDiagnosticsResilienceTests.cs deterministic postimage: $diagnosticsTestsPostHash"

$expectedFiles = @{
    'src/CloudScribe.App/CloudScribeApplication.axaml' = '997cc05fe07360086dccaaada1001f5bafbc82d04dbc9d466b57eb27f2c18eac'
    'tools/verify_stage2_visual_evidence.py' = 'da1cd6fd796a80f14af7e41c5d26143b6e0a05f60d95322e773c68aa898dd37c'
    'tools/verify_stage2_source.py' = '1a1a60252ca9355f3d08c864f4c52d11b26b9cacad17ffa323e343b7733a6768'
    'tests/test_verification_tools.py' = 'd7b42c460cf55658e3c3ebebeeab09ae99931a3688d488832faab7e30c8598d6'
    'tests/CloudScribe.Architecture.Tests/VisualCaptureSizingContractTests.cs' = '756e8212269c1d996efbd74f15dc8808edcce1e305f589d419f01863d78a5035'
}
foreach ($relative in $expectedFiles.Keys) {
    Assert-Sha256 (Join-Path $SourceRoot $relative) $expectedFiles[$relative] $relative
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
    Invoke-Checked "Locked restore $project" { dotnet restore $project --locked-mode --disable-parallel --configfile NuGet.config }
}
foreach ($configuration in @('Debug','Release')) {
    foreach ($project in $buildOrder) {
        Invoke-Checked "$configuration build $project" {
            dotnet build $project -c $configuration --no-restore --disable-build-servers -m:1 -nodeReuse:false `
                -p:BuildProjectReferences=false -p:BuildInParallel=false -p:UseSharedCompilation=false
        }
    }
}

$resultRoot = Join-Path $env:RUNNER_TEMP 'test-results'
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
for ($i = 0; $i -lt $testProjects.Count; $i++) {
    $dir = Join-Path $resultRoot $i
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Invoke-Checked "Test $($testProjects[$i])" {
        dotnet test $testProjects[$i] -c Release --no-build --no-restore -m:1 -nodeReuse:false `
            -p:UseSharedCompilation=false --results-directory $dir --logger 'trx;LogFileName=stage2-tests.trx'
    }
}
$total = 0; $failed = 0; $skipped = 0; $trxCount = 0
foreach ($trx in Get-ChildItem $resultRoot -Filter '*.trx' -File -Recurse) {
    $trxCount++
    [xml]$xml = Get-Content $trx.FullName -Raw
    $c = $xml.SelectSingleNode("//*[local-name()='Counters']")
    if (-not $c) { throw "Missing test counters in $($trx.FullName)." }
    $total += [int]$c.GetAttribute('total')
    $failed += [int]$c.GetAttribute('failed')
    $skip = $c.GetAttribute('skipped'); if ($skip) { $skipped += [int]$skip }
}
if ($trxCount -ne 4 -or $total -ne 147 -or $failed -ne 0 -or $skipped -ne 0) {
    throw "Unexpected .NET test inventory: trx=$trxCount total=$total failed=$failed skipped=$skipped; expected 4/147/0/0."
}
Write-Host 'PASS: 147/147 .NET tests.'

Invoke-Checked 'dotnet format --verify-no-changes' { dotnet format CloudScribe.sln --verify-no-changes --no-restore }

$scanRoot = Join-Path $env:RUNNER_TEMP 'package-scans'
$scanLogRoot = Join-Path $env:RUNNER_TEMP 'package-scan-logs'
New-Item -ItemType Directory -Path $scanRoot, $scanLogRoot -Force | Out-Null
for ($i = 0; $i -lt $projects.Count; $i++) {
    $vuln = Join-Path $scanRoot "$i-vulnerable.json"
    $err = Join-Path $scanLogRoot "$i-vulnerable.stderr.log"
    & pwsh -NoProfile -File scripts/invoke-nuget-audit-scan.ps1 -Project $projects[$i] 1> $vuln 2> $err
    if ($LASTEXITCODE -ne 0) { if (Test-Path $err) { Get-Content $err }; throw "Vulnerability scan failed: $($projects[$i])" }
    & dotnet package list --project $projects[$i] --deprecated --include-transitive --no-restore --format json --output-version 1 |
        Set-Content -LiteralPath (Join-Path $scanRoot "$i-deprecated.json") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Deprecation scan failed: $($projects[$i])" }
}
Invoke-Checked 'Validate paired package scan JSON reports' { python tools/verify_dotnet_package_scan.py $scanRoot }

$publishRoot = Join-Path $env:RUNNER_TEMP 'CloudScribe-publish'
Invoke-Checked 'Publish Windows candidate' {
    pwsh -NoProfile -File scripts/publish-stage2-windows.ps1 -OutputDirectory $publishRoot -Configuration Release -Status verification-pending
}
Invoke-Checked 'Native Windows launch + secondary activation smoke' { pwsh -NoProfile -File scripts/smoke-stage1-windows.ps1 }
$screenRoot = Join-Path $env:RUNNER_TEMP 'stage2-screenshots'
Invoke-Checked 'Native 17-case Stage 2 visual capture + editor contrast audit' { pwsh -NoProfile -File scripts/capture-stage2-windows.ps1 $screenRoot }
Invoke-Checked 'Final source manifest re-check' { python tools/update_sha256_manifest.py --check }

Write-Host 'CLOUDSCRIBE_STAGE2_FOCUS_FIX_NATIVE_WINDOWS_CERTIFICATION=PASS'
