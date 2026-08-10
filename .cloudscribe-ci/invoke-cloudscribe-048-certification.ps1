param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceScript = Join-Path $PSScriptRoot 'run-cloudscribe-048-certification.ps1'
if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
    throw "Certification script is missing: $sourceScript"
}

$text = [IO.File]::ReadAllText($sourceScript).Replace("`r`n", "`n").Replace("`r", "`n")
$startMarker = '# Four diagnostics-writer integration tests passed individually and as a class, but failed only'
$endMarker = '$expectedFiles = @{'
$startIndex = $text.IndexOf($startMarker, [StringComparison]::Ordinal)
if ($startIndex -lt 0) {
    throw 'Obsolete diagnostics serialization block start marker was not found.'
}
$secondStartIndex = $text.IndexOf($startMarker, $startIndex + $startMarker.Length, [StringComparison]::Ordinal)
if ($secondStartIndex -ge 0) {
    throw 'Obsolete diagnostics serialization block start marker was found more than once.'
}
$endIndex = $text.IndexOf($endMarker, $startIndex, [StringComparison]::Ordinal)
if ($endIndex -le $startIndex) {
    throw 'Expected-files marker after obsolete diagnostics serialization block was not found.'
}

$replacement = @'
# The diagnostics writer integration tests passed individually and as a complete class, but
# timed out only while unrelated xUnit collections ran concurrently on the hosted runner.
# Preserve every test and assertion and serialize this test assembly instead of weakening
# product behavior or extending production timeouts. xUnit's assembly-level switch removes
# cross-collection scheduler contention without introducing an analyzer-conflicting helper type.
$diagnosticsTestsPath = Join-Path $SourceRoot 'tests/CloudScribe.Infrastructure.Tests/StartupAndDiagnosticsResilienceTests.cs'
Assert-Sha256 $diagnosticsTestsPath 'd0d6a3d8e2a88aa09ecb1fb6a00943d71a6b4c92d0d36e37e94c0b5e83edb764' 'StartupAndDiagnosticsResilienceTests.cs preimage'
$diagnosticsTestsText = [IO.File]::ReadAllText($diagnosticsTestsPath).Replace("`r`n", "`n").Replace("`r", "`n")
$namespaceAnchor = 'namespace CloudScribe.Infrastructure.Tests;'
if (($diagnosticsTestsText.Split($namespaceAnchor).Length - 1) -ne 1) {
    throw 'Expected exactly one CloudScribe.Infrastructure.Tests namespace declaration.'
}
$parallelizationAttribute = '[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]'
if ($diagnosticsTestsText.Contains($parallelizationAttribute, [StringComparison]::Ordinal)) {
    throw 'Infrastructure test parallelization attribute unexpectedly already exists in the exact preimage.'
}
$diagnosticsTestsText = $diagnosticsTestsText.Replace(
    $namespaceAnchor,
    $parallelizationAttribute + "`n`n" + $namespaceAnchor,
    [StringComparison]::Ordinal)
[IO.File]::WriteAllText($diagnosticsTestsPath, $diagnosticsTestsText, [Text.UTF8Encoding]::new($false))
$diagnosticsTestsPostHash = (Get-FileHash -LiteralPath $diagnosticsTestsPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified StartupAndDiagnosticsResilienceTests.cs serialized-test postimage: $diagnosticsTestsPostHash"

'@

$text = $text.Substring(0, $startIndex) + $replacement + $text.Substring($endIndex)
if ($text.Contains('DiagnosticWriterIntegrationCollection', [StringComparison]::Ordinal)) {
    throw 'Obsolete analyzer-conflicting collection-definition type remains in patched certification script.'
}
if ($text.Contains('[Collection("Diagnostic writer integration")]', [StringComparison]::Ordinal)) {
    throw 'Obsolete targeted diagnostic writer collection attribute remains in patched certification script.'
}

$tempScript = Join-Path $env:RUNNER_TEMP 'run-cloudscribe-048-certification-analyzer-clean.ps1'
[IO.File]::WriteAllText($tempScript, $text, [Text.UTF8Encoding]::new($false))
Write-Host "Prepared analyzer-clean certification driver: $tempScript"

& pwsh -NoProfile -File $tempScript -SourceRoot $SourceRoot
if ($LASTEXITCODE -ne 0) {
    throw "Analyzer-clean native certification failed with exit code $LASTEXITCODE."
}
