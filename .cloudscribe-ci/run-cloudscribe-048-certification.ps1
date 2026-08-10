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
$carrierCommit = '94f1991bf975379e161a5bcaf918ffb410a10d27'
$carrierRoot = "https://raw.githubusercontent.com/sohweiyu2023/CloudScribe-Pro/$carrierCommit/.cloudscribe-ci/focus-fix-carrier"
$carrierB64 = Join-Path $env:RUNNER_TEMP 'cloudscribe-focus-fix.b64'
$carrierXz = Join-Path $env:RUNNER_TEMP 'cloudscribe-focus-fix.tar.xz'
Remove-Item $carrierB64, $carrierXz -Force -ErrorAction SilentlyContinue
foreach ($part in @('000.b64','001.b64','002.b64')) {
    $partPath = Join-Path $env:RUNNER_TEMP $part
    Invoke-WebRequest -Uri "$carrierRoot/$part" -OutFile $partPath
    [IO.File]::AppendAllText($carrierB64, ([IO.File]::ReadAllText($partPath)).Trim())
}
[IO.File]::WriteAllBytes($carrierXz, [Convert]::FromBase64String([IO.File]::ReadAllText($carrierB64)))
if ((Get-Item $carrierXz).Length -ne 21144) { throw 'Focus-fix carrier length mismatch.' }
Assert-Sha256 $carrierXz 'f558d9cfafcc270a33b13e91a52bdb7615592f92c90a4cfdef1af3ed401bbb41' 'focus-fix carrier'
& tar.exe -xJf $carrierXz -C $SourceRoot
if ($LASTEXITCODE -ne 0) { throw "Focus-fix carrier extraction failed: $LASTEXITCODE" }

$expectedFiles = @{
    'src/CloudScribe.App/CloudScribeApplication.axaml' = '997cc05fe07360086dccaaada1001f5bafbc82d04dbc9d466b57eb27f2c18eac'
    'src/CloudScribe.App/MainWindow.VisualCapture.cs' = 'd97162ececc7afa82a2c6a67e2eb6a48aec279872463cfa7a7b19249567687a5'
    'tools/verify_stage2_visual_evidence.py' = 'b17966e22d7cc7c584be5bf7df539a337eca496d838c5dfcfc2c4892c9378627'
    'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs' = '1cdc19200404c5f8f3f51cdad08d95d7b47bcb4e8ce00d8e03b51584158e4dd1'
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
