param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot 'CloudScribe.sln') -PathType Leaf)) {
    throw "CloudScribe source root is invalid: $SourceRoot"
}

$payloadRoot = Join-Path $PSScriptRoot 'stage2-focus-payload'
$partNames = @('00.txt','01.txt','02.txt','03.txt','04.txt','05.txt')
$expectedPartHashes = @(
    '132f109df8c2f714306ae204c4c64db701bf138e546f31ad0f3cf08527e2ee1a',
    '7eebe27c9e4d275336a13e4eecdffed573392352346b640965e0246f8366c181',
    '30d8057694d247ec3d285863b436f2fd7578ff0d42753d0356d0d32dd2eec688',
    'cb96c9859fcf93bba0c331d15a481eeb913a9f4aefb46f56f90e5fbccacda379',
    'b3c304a106e74e755b29fa8efb2480b2113a5babf057d1e498134df4729ec8c2',
    '01934b810766638d5f9784685b02b43059e0a3ebc17a82340af091aefaf3e79d'
)
$builder = [Text.StringBuilder]::new()
for ($i = 0; $i -lt $partNames.Count; $i++) {
    $path = Join-Path $payloadRoot $partNames[$i]
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Stage 2 focus repair payload part: $path" }
    $part = [IO.File]::ReadAllText($path).Trim()
    $partHash = ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($part)) | ForEach-Object ToString x2) -join ''
    if ($partHash -ne $expectedPartHashes[$i]) { throw "Stage 2 focus repair payload part hash mismatch for $($partNames[$i]): $partHash" }
    [void]$builder.Append($part)
}

$compressed = [Convert]::FromBase64String($builder.ToString())
$compressedSha = ([Security.Cryptography.SHA256]::HashData($compressed) | ForEach-Object ToString x2) -join ''
if ($compressedSha -ne '792b2597d743f5e1ede4580c59f86db48ce4b3ec0fd52336bcb0ff7826eafa12') {
    throw "Stage 2 focus repair compressed payload hash mismatch: $compressedSha"
}

$input = [IO.MemoryStream]::new($compressed)
$output = [IO.MemoryStream]::new()
$gzip = [IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)
try { $gzip.CopyTo($output) } finally { $gzip.Dispose(); $input.Dispose() }
$patchBytes = $output.ToArray(); $output.Dispose()
$patchSha = ([Security.Cryptography.SHA256]::HashData($patchBytes) | ForEach-Object ToString x2) -join ''
if ($patchSha -ne '1b7322cc773969e81f6519cbdc901ffd7daa9455928e68d336ceaef50035e4ac') {
    throw "Stage 2 focus repair patch hash mismatch: $patchSha"
}
$patchPath = Join-Path $env:RUNNER_TEMP 'cloudscribe-stage2-focus-acceptance.patch'
[IO.File]::WriteAllBytes($patchPath,$patchBytes)

Push-Location -LiteralPath $SourceRoot
try {
    & git apply --check --whitespace=nowarn $patchPath
    if ($LASTEXITCODE -ne 0) { throw "Stage 2 focus repair patch preflight failed: $LASTEXITCODE" }
    & git apply --whitespace=nowarn $patchPath
    if ($LASTEXITCODE -ne 0) { throw "Stage 2 focus repair patch failed: $LASTEXITCODE" }
}
finally { Pop-Location }

# Git's working-tree line-ending conversion differs between the Linux authoring environment
# and Windows certification runners. Canonicalize the repaired text postimages to UTF-8
# without BOM and LF before byte-hash validation, so the frozen source archive is identical
# regardless of checkout platform instead of accepting platform-specific CRLF hashes.
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$normalizedFiles = @(
    'src/CloudScribe.App/MainWindow.axaml',
    'src/CloudScribe.App/MainWindow.VisualCapture.cs',
    'tools/verify_stage2_visual_evidence.py',
    'tools/verify_stage2_source.py',
    'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs',
    'tools/run_python_regression_shards.py',
    'SESSION_STATE.json'
)
foreach ($relative in $normalizedFiles) {
    $target = Join-Path $SourceRoot $relative
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Stage 2 focus repair output is missing: $relative" }
    $text = [IO.File]::ReadAllText($target).Replace("`r`n","`n").Replace("`r","`n")
    [IO.File]::WriteAllText($target,$text,$utf8NoBom)
}

$expected = @{
    'src/CloudScribe.App/MainWindow.axaml' = '146e7395924c757721de6e7e89d0a6f833192c861fd794ed2e64909ec0a9c65d'
    'src/CloudScribe.App/MainWindow.VisualCapture.cs' = 'd140d1836ee9070149afeafd6dd95c1e1f36deb2d3ab60033741f4b13c0d9a28'
    'tools/verify_stage2_visual_evidence.py' = '756c8d387e4af76ceef10c7413d3356ac8018d04248b45777c7c887947b6629a'
    'tools/verify_stage2_source.py' = '42bd813fa5e5d697fceec555e08b276cfc7da07df4c1f4b63d1eddb762d3fd57'
    'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs' = '2badcd169ecaed5444f7b12fcd20ff304f6d93081d90f1a9426ef9edd9abd2fc'
    'tools/run_python_regression_shards.py' = '03d277226cdf42a49070ffd5104231c798c5cdaa0be223063f0cdea33fd2deda'
    'SESSION_STATE.json' = '7a4db331b97fde556892e01b15b44f251b38fb2130bdc1144c213c0db117855a'
}
foreach ($relative in $expected.Keys) {
    $actual=(Get-FileHash -LiteralPath (Join-Path $SourceRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$relative]) { throw "Stage 2 focus repair postimage mismatch for ${relative}: $actual" }
}

Write-Host 'CLOUDSCRIBE_STAGE2_FOCUS_ACCEPTANCE_REPAIR=PASS'
