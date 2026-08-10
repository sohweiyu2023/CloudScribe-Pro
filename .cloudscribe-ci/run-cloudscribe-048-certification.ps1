param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location -LiteralPath $SourceRoot

function Invoke-Checked {
    param([Parameter(Mandatory=$true)][string]$Label,[Parameter(Mandatory=$true)][scriptblock]$Action)
    Write-Host "== $Label =="
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

$sdk = (& dotnet --version).Trim()
if ($sdk -ne '10.0.302') { throw "Expected .NET SDK 10.0.302, got $sdk." }

# Deterministic regression-test repair for a Windows filesystem race discovered by the
# first native certification run. The carrier is immutable; only the exact known
# preimage may be changed, and the exact postimage hash is verified before any build.
$rotationTestPath = Join-Path $SourceRoot 'tests/CloudScribe.Infrastructure.Tests/StartupAndDiagnosticsResilienceTests.cs'
$rotationTestBefore = (Get-FileHash -LiteralPath $rotationTestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$rotationTestOldHash = '0f989eb73e648484086ddbd473e0fc1ae4defd9f20e76c66b770ecb4434f6ddd'
$rotationTestNewHash = 'd0d6a3d8e2a88aa09ecb1fb6a00943d71a6b4c92d0d36e37e94c0b5e83edb764'
if ($rotationTestBefore -eq $rotationTestOldHash) {
    $text = [IO.File]::ReadAllText($rotationTestPath)
    $old = "if (files.Length == 2 && files.All(file => file.Length <= 1024 * 1024))"
    $new = "if (files.Length == 2 && files.All(file => file.Length is >= 1 and <= 1024 * 1024))"
    if (-not $text.Contains($old)) { throw 'Known rotation-test preimage hash matched but expected assertion text was absent.' }
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($rotationTestPath, $text, [Text.UTF8Encoding]::new($false))
}
elseif ($rotationTestBefore -ne $rotationTestNewHash) {
    throw "Unexpected rotation-test source hash before certification repair: $rotationTestBefore"
}
$rotationTestAfter = (Get-FileHash -LiteralPath $rotationTestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($rotationTestAfter -ne $rotationTestNewHash) { throw "Rotation-test repair hash mismatch: $rotationTestAfter" }
Write-Host "Verified deterministic rotation-test repair: $rotationTestAfter"

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

foreach ($p in $projects) {
    & dotnet build-server shutdown | Out-Null
    Invoke-Checked "Locked restore $p" { dotnet restore $p --locked-mode --disable-parallel --configfile NuGet.config }
}
foreach ($configuration in @('Debug','Release')) {
    foreach ($p in $buildOrder) {
        Invoke-Checked "$configuration build $p" {
            dotnet build $p -c $configuration --no-restore --disable-build-servers -m:1 -nodeReuse:false -p:BuildProjectReferences=false -p:BuildInParallel=false -p:UseSharedCompilation=false
        }
    }
}

$resultRoot = Join-Path $env:RUNNER_TEMP 'test-results'
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
for ($i = 0; $i -lt $testProjects.Count; $i++) {
    $dir = Join-Path $resultRoot $i
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Invoke-Checked "Test $($testProjects[$i])" {
        dotnet test $testProjects[$i] -c Release --no-build --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --results-directory $dir --logger 'trx;LogFileName=stage2-tests.trx'
    }
}
$total = 0; $failed = 0; $skipped = 0; $trxCount = 0
foreach ($trx in Get-ChildItem -LiteralPath $resultRoot -Filter '*.trx' -File -Recurse) {
    $trxCount++
    [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw
    $c = $xml.SelectSingleNode("//*[local-name()='Counters']")
    if (-not $c) { throw "Missing counters in $($trx.FullName)." }
    $totalValue = $c.GetAttribute('total')
    $failedValue = $c.GetAttribute('failed')
    $skippedValue = $c.GetAttribute('skipped')
    if ([string]::IsNullOrWhiteSpace($totalValue) -or [string]::IsNullOrWhiteSpace($failedValue)) { throw "Missing required counter attributes in $($trx.FullName)." }
    $total += [int]$totalValue
    $failed += [int]$failedValue
    if (-not [string]::IsNullOrWhiteSpace($skippedValue)) { $skipped += [int]$skippedValue }
}
if ($trxCount -ne 4 -or $total -ne 146 -or $failed -ne 0 -or $skipped -ne 0) {
    throw "Unexpected test inventory: trx=$trxCount total=$total failed=$failed skipped=$skipped; expected 4/146/0/0."
}
Write-Host 'PASS: 146/146 .NET tests.'

Invoke-Checked 'dotnet format --verify-no-changes' { dotnet format CloudScribe.sln --verify-no-changes --no-restore }

$scanRoot = Join-Path $env:RUNNER_TEMP 'package-scans'
$vulnDir = Join-Path $scanRoot 'vulnerable'
$depDir = Join-Path $scanRoot 'deprecated'
New-Item -ItemType Directory -Path $vulnDir,$depDir -Force | Out-Null
for ($i = 0; $i -lt $projects.Count; $i++) {
    $vuln = Join-Path $vulnDir "$i.json"
    $vulnErr = Join-Path $vulnDir "$i.stderr.log"
    Write-Host "== Vulnerability scan $($projects[$i]) =="
    & pwsh -NoProfile -File scripts/invoke-nuget-audit-scan.ps1 -Project $projects[$i] 1> $vuln 2> $vulnErr
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $vulnErr) { Get-Content -LiteralPath $vulnErr }
        throw "NuGet vulnerability audit failed: $($projects[$i])"
    }
    Write-Host "== Deprecation scan $($projects[$i]) =="
    & dotnet package list --project $projects[$i] --deprecated --include-transitive --no-restore --format json --output-version 1 | Set-Content -LiteralPath (Join-Path $depDir "$i.json") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "NuGet deprecation scan failed: $($projects[$i])" }
}
Invoke-Checked 'Validate vulnerability JSON reports' { python tools/verify_dotnet_package_scan.py --mode vulnerable --kind vulnerability --input-directory $vulnDir --expected-project-count 9 }
Invoke-Checked 'Validate deprecation JSON reports' { python tools/verify_dotnet_package_scan.py --mode deprecated --kind deprecation --input-directory $depDir --expected-project-count 9 }

$publish = Join-Path $env:RUNNER_TEMP 'CloudScribe-publish'
Invoke-Checked 'Publish Windows candidate' { pwsh -NoProfile -File scripts/publish-stage2-windows.ps1 -OutputDirectory $publish -Configuration Release -Status verification-pending }
Invoke-Checked 'Native Windows launch + secondary activation smoke' { pwsh -NoProfile -File scripts/smoke-stage1-windows.ps1 }
$screens = Join-Path $env:RUNNER_TEMP 'stage2-screenshots'
Invoke-Checked 'Native 14-case Stage 2 visual capture' { pwsh -NoProfile -File scripts/capture-stage2-windows.ps1 $screens }
Invoke-Checked 'Final source manifest re-check' { python tools/update_sha256_manifest.py --check }

Write-Host 'CLOUDSCRIBE_STAGE2_NATIVE_WINDOWS_CERTIFICATION=PASS'
