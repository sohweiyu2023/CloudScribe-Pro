param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$wrapperPath = Join-Path $SourceRoot 'scripts/invoke-nuget-audit-scan.ps1'
$overlayPath = Join-Path $PSScriptRoot 'repair-overlay/invoke-nuget-audit-scan.ps1'
$wrapperOldHash = 'ab0ddeb88a7ee027f41348efc6bb8499a29cc21095cd19a3e8beded1e7426584'
$wrapperNewHash = 'de5981b2ef579c6b85261cd5ef8543cf937b79243688804777987c2274e41841'
$overlayHash = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($overlayHash -ne $wrapperNewHash) { throw "Windows audit repair overlay hash mismatch: $overlayHash" }
$wrapperBefore = (Get-FileHash -LiteralPath $wrapperPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($wrapperBefore -eq $wrapperOldHash) {
    Copy-Item -LiteralPath $overlayPath -Destination $wrapperPath -Force
}
elseif ($wrapperBefore -ne $wrapperNewHash) {
    throw "Unexpected Windows audit wrapper preimage: $wrapperBefore"
}
$wrapperAfter = (Get-FileHash -LiteralPath $wrapperPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($wrapperAfter -ne $wrapperNewHash) { throw "Windows audit wrapper repair hash mismatch: $wrapperAfter" }
Write-Host "Verified Windows audit wrapper follow-on repair: $wrapperAfter"

$architecturePath = Join-Path $SourceRoot 'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs'
$architectureOldHash = '2a3c903ea38a4bf2a4c5012dff80906366fcbe8c06369a03a61661476d16402b'
$architectureNewHash = 'f92c0f76d5e278efe91e071056bb853117100a6b0702463d4fbd35ca18fe1819'
$architectureBefore = (Get-FileHash -LiteralPath $architecturePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($architectureBefore -eq $architectureOldHash) {
    $text = [IO.File]::ReadAllText($architecturePath)
    $line1 = '        Assert.Contains("[IO.FileAttributes]::ReparsePoint", windowsAudit, StringComparison.Ordinal);'
    $line2 = '        Assert.Contains("dotnet restore $projectPath", windowsAudit, StringComparison.Ordinal);'
    $old = $line1 + "`n" + $line2
    $new = $line1 + "`n" +
        '        Assert.Contains("$cursor = $projectItem.Directory", windowsAudit, StringComparison.Ordinal);' + "`n" +
        '        Assert.DoesNotContain("$cursor = $projectItem\n", windowsAudit, StringComparison.Ordinal);' + "`n" +
        $line2
    if (-not $text.Contains($old)) { throw 'Known architecture-test preimage hash matched but expected audit containment assertion block was absent.' }
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($architecturePath, $text, [Text.UTF8Encoding]::new($false))
}
elseif ($architectureBefore -ne $architectureNewHash) {
    throw "Unexpected architecture-test preimage: $architectureBefore"
}
$architectureAfter = (Get-FileHash -LiteralPath $architecturePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($architectureAfter -ne $architectureNewHash) { throw "Architecture-test repair hash mismatch: $architectureAfter" }
Write-Host "Verified compiled architecture regression update: $architectureAfter"

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-cloudscribe-048-certification-core.ps1') -SourceRoot $SourceRoot
exit $LASTEXITCODE
