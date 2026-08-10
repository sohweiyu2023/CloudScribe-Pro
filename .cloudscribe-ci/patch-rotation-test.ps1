param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = Join-Path $SourceRoot 'tests/CloudScribe.Infrastructure.Tests/StartupAndDiagnosticsResilienceTests.cs'
$oldHash = '0f989eb73e648484086ddbd473e0fc1ae4defd9f20e76c66b770ecb4434f6ddd'
$newHash = 'd0d6a3d8e2a88aa09ecb1fb6a00943d71a6b4c92d0d36e37e94c0b5e83edb764'
$before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()

if ($before -eq $oldHash) {
    $text = [IO.File]::ReadAllText($path)
    $old = 'if (files.Length == 2 && files.All(file => file.Length <= 1024 * 1024))'
    $new = 'if (files.Length == 2 && files.All(file => file.Length is >= 1 and <= 1024 * 1024))'
    if (-not $text.Contains($old)) {
        throw 'Known rotation-test preimage hash matched but expected assertion text was absent.'
    }
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
elif ($before -ne $newHash) {
    throw "Unexpected rotation-test source hash before certification repair: $before"
}

$after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
if ($after -ne $newHash) {
    throw "Rotation-test repair hash mismatch: $after"
}
Write-Host "Verified deterministic rotation-test repair: $after"
