param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Bootstrap the material-release verifier set from an immutable helper commit, then
# delegate to the previously native-Windows-certified repair wrapper from that same
# commit. The certified wrapper intentionally overlays its own historical verification
# fixtures, so the material tools are installed once before syntax validation and again
# after native certification to ensure the exact source ZIP contains the current material
# gate implementations rather than an older verifier carried by the visual-focus overlay.
$carrierCommit = 'd46a92633debde94ed3a805bc2571af0ccaeb451'
$carrierRoot = "https://raw.githubusercontent.com/sohweiyu2023/CloudScribe-Pro/$carrierCommit/.cloudscribe-ci"
$toolNames = @(
    'verify_dotnet_sdk_version.py',
    'verify_project_dependencies.py',
    'verify_repository.py',
    'verify_stage1_source.py',
    'verify_stage2_source.py',
    'verify_stage2_evidence_inventory.py',
    'run_python_regression_shards.py',
    'create_source_archive.py',
    'verify_source_release.py'
)
$toolsDirectory = Join-Path $SourceRoot 'tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null

function Install-MaterialTools {
    param([string]$Phase)
    foreach ($name in $toolNames) {
        $destination = Join-Path $toolsDirectory $name
        Invoke-WebRequest -Uri "$carrierRoot/material-tools/$name" -OutFile $destination
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or (Get-Item -LiteralPath $destination).Length -le 0) {
            throw "Material verifier download failed or was empty during ${Phase}: $name"
        }
    }

    $compileArguments = @('-m','py_compile') + @($toolNames | ForEach-Object { Join-Path $toolsDirectory $_ })
    & python @compileArguments
    if ($LASTEXITCODE -ne 0) { throw "Material verifier syntax validation failed during ${Phase}: $LASTEXITCODE" }
    Write-Host "Installed and syntax-checked $($toolNames.Count) material/release verifiers during $Phase from immutable commit $carrierCommit."
}

Install-MaterialTools -Phase 'pre-certification bootstrap'

# The certified wrapper resolves its driver through $PSScriptRoot, so restore both files
# into the same isolated directory. This preserves the exact sibling-file contract of the
# already-certified wrapper instead of rewriting its behavior.
$coreDirectory = Join-Path $env:RUNNER_TEMP 'cloudscribe-048-certified-core'
Remove-Item -LiteralPath $coreDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $coreDirectory -Force | Out-Null
$coreWrapper = Join-Path $coreDirectory 'invoke-cloudscribe-048-certification.ps1'
$coreDriver = Join-Path $coreDirectory 'run-cloudscribe-048-certification.ps1'
Invoke-WebRequest -Uri "$carrierRoot/invoke-cloudscribe-048-certification.ps1" -OutFile $coreWrapper
Invoke-WebRequest -Uri "$carrierRoot/run-cloudscribe-048-certification.ps1" -OutFile $coreDriver
foreach ($required in @($coreWrapper, $coreDriver)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf) -or (Get-Item -LiteralPath $required).Length -le 0) {
        throw "Certified core script download failed or was empty: $required"
    }
}
Write-Host "Restored certified wrapper/driver sibling pair from immutable commit $carrierCommit."

& pwsh -NoProfile -File $coreWrapper -SourceRoot $SourceRoot
if ($LASTEXITCODE -ne 0) {
    throw "Certified core wrapper failed with exit code $LASTEXITCODE."
}

# The focus-fix carrier used by the certified core contains an older Stage 2 source verifier.
# Re-apply the complete material-tool set after native certification, then apply the Windows
# source-handoff usability repair (root CMD launcher + guide + regression contracts). Bind all
# final bytes into SHA256SUMS.txt and run the fast source/material contracts before the outer
# workflow freezes the ZIP. The subsequent fresh-extracted gate rebuilds/retests these exact bytes.
Install-MaterialTools -Phase 'post-certification final-source overlay'
$buildLauncherOverlay = Join-Path $PSScriptRoot 'apply-build-launcher-overlay.ps1'
if (-not (Test-Path -LiteralPath $buildLauncherOverlay -PathType Leaf)) {
    throw "Windows build-launcher overlay is missing: $buildLauncherOverlay"
}
& pwsh -NoProfile -ExecutionPolicy Bypass -File $buildLauncherOverlay -SourceRoot $SourceRoot
if ($LASTEXITCODE -ne 0) {
    throw "Windows build-launcher overlay failed with exit code $LASTEXITCODE."
}

Push-Location -LiteralPath $SourceRoot
try {
    & python tools/update_sha256_manifest.py
    if ($LASTEXITCODE -ne 0) { throw "Final material-tool manifest generation failed: $LASTEXITCODE" }
    & python tools/update_sha256_manifest.py --check
    if ($LASTEXITCODE -ne 0) { throw "Final material-tool manifest verification failed: $LASTEXITCODE" }

    foreach ($command in @(
        @('tools/verify_project_dependencies.py'),
        @('tools/verify_stage1_source.py'),
        @('tools/verify_stage2_source.py'),
        @('tools/verify_repository.py'),
        @('tools/run_python_regression_shards.py','--all')
    )) {
        & python @command
        if ($LASTEXITCODE -ne 0) { throw "Post-certification material contract failed: python $($command -join ' ') (exit $LASTEXITCODE)" }
    }

    & python tools/update_sha256_manifest.py --check
    if ($LASTEXITCODE -ne 0) { throw "Source manifest changed during post-certification material contracts: $LASTEXITCODE" }
}
finally {
    Pop-Location
}
Write-Host 'CLOUDSCRIBE_POST_CERTIFICATION_MATERIAL_SOURCE_OVERLAY=PASS'
