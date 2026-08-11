param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$carrierRoot = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot 'CloudScribe.sln') -PathType Leaf)) {
    throw "CloudScribe source root is invalid: $SourceRoot"
}

$launcherSource = Join-Path $carrierRoot 'BUILD-CLOUDSCRIBE-WINDOWS.cmd'
$launcherText = [IO.File]::ReadAllText($launcherSource).Replace("`r`n","`n").Replace("`r","`n")
[IO.File]::WriteAllText(
    (Join-Path $SourceRoot 'BUILD-CLOUDSCRIBE-WINDOWS.cmd'),
    $launcherText.Replace("`n","`r`n"),
    [Text.Encoding]::ASCII)

$guideSource = Join-Path $carrierRoot 'BUILDING-WINDOWS.txt'
$guideText = [IO.File]::ReadAllText($guideSource).Replace("`r`n","`n").Replace("`r","`n")
[IO.File]::WriteAllText((Join-Path $SourceRoot 'BUILDING-WINDOWS.txt'),$guideText,$utf8NoBom)

$architecturePath = Join-Path $SourceRoot 'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs'
$architecture = [IO.File]::ReadAllText($architecturePath).Replace("`r`n","`n").Replace("`r","`n")
if (-not $architecture.Contains('string windowsBuildLauncher = File.ReadAllText')) {
    $readAnchor = '        string windowsCapture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-windows.ps1"));' + "`n" +
                  '        string windowsPublish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-stage2-windows.ps1"));'
    $readReplacement = $readAnchor + "`n" +
                  '        string windowsBuildLauncher = File.ReadAllText(Path.Combine(repositoryRoot, "BUILD-CLOUDSCRIBE-WINDOWS.cmd"));' + "`n" +
                  '        string windowsBuildGuide = File.ReadAllText(Path.Combine(repositoryRoot, "BUILDING-WINDOWS.txt"));'
    if (-not $architecture.Contains($readAnchor)) { throw 'Build-launcher architecture read anchor was not found.' }
    $architecture = $architecture.Replace($readAnchor,$readReplacement)

    $assertAnchor = '        Assert.Contains("CloudScribe.exe", windowsPublish, StringComparison.Ordinal);' + "`n" +
                    '        Assert.Contains("LATEST-CLOUDSCRIBE-EXE.txt", windows, StringComparison.Ordinal);'
    $assertReplacement = '        Assert.Contains("CloudScribe.exe", windowsPublish, StringComparison.Ordinal);' + "`n" +
                    '        AssertWindowsBuildLauncherContract(windowsBuildLauncher, windowsBuildGuide);' + "`n" +
                    '        Assert.Contains("LATEST-CLOUDSCRIBE-EXE.txt", windows, StringComparison.Ordinal);'
    if (-not $architecture.Contains($assertAnchor)) { throw 'Build-launcher architecture assertion anchor was not found.' }
    $architecture = $architecture.Replace($assertAnchor,$assertReplacement)

    $helperAnchor = '    private static void AssertNuGetAuditRetryBoundary(string auditWrapper)' + "`n"
    $helper = '    private static void AssertWindowsBuildLauncherContract(string windowsBuildLauncher, string windowsBuildGuide)' + "`n" +
              '    {' + "`n" +
              '        Assert.Contains("-ExecutionPolicy Bypass", windowsBuildLauncher, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("if errorlevel 1 goto :publish_failed_pop", windowsBuildLauncher, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("CloudScribe.exe", windowsBuildLauncher, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("RUN-CLOUDSCRIBE.cmd", windowsBuildLauncher, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("CLOUDSCRIBE_NO_OPEN", windowsBuildLauncher, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("src\\CloudScribe.App\\CloudScribe.App.csproj", windowsBuildGuide, StringComparison.Ordinal);' + "`n" +
              '        Assert.Contains("MachinePolicy or UserPolicy", windowsBuildGuide, StringComparison.Ordinal);' + "`n" +
              '    }' + "`n`n"
    if (-not $architecture.Contains($helperAnchor)) { throw 'Build-launcher architecture helper anchor was not found.' }
    $architecture = $architecture.Replace($helperAnchor,$helper + $helperAnchor)
    [IO.File]::WriteAllText($architecturePath,$architecture,$utf8NoBom)
}

$runnerPath = Join-Path $SourceRoot 'tools/run_python_regression_shards.py'
$runner = [IO.File]::ReadAllText($runnerPath).Replace("`r`n","`n").Replace("`r","`n")
$runner = $runner.Replace('EXPECTED_CHECK_COUNT = 149','EXPECTED_CHECK_COUNT = 151')
if (-not $runner.Contains('"BUILD-CLOUDSCRIBE-WINDOWS.cmd"')) {
    $rootAnchor = '    "SHA256SUMS.txt",' + "`n" + ')'
    $rootReplacement = '    "SHA256SUMS.txt",' + "`n" +
                       '    "BUILD-CLOUDSCRIBE-WINDOWS.cmd",' + "`n" +
                       '    "BUILDING-WINDOWS.txt",' + "`n" + ')'
    if (-not $runner.Contains($rootAnchor)) { throw 'Material runner root-file anchor was not found.' }
    $runner = $runner.Replace($rootAnchor,$rootReplacement)
}
$runner = $runner.Replace('deterministic 149-check Stage 2','deterministic 151-check Stage 2')
[IO.File]::WriteAllText($runnerPath,$runner,$utf8NoBom)

$statePath = Join-Path $SourceRoot 'SESSION_STATE.json'
$state = [IO.File]::ReadAllText($statePath).Replace("`r`n","`n").Replace("`r","`n")
$state = $state.Replace('canonical 149-test/15-shard inventory','canonical 151-test/15-shard inventory')
$state = $state.Replace('complete 149-test suite','complete 151-test suite')
[IO.File]::WriteAllText($statePath,$state,$utf8NoBom)

$expected = @{
    'BUILD-CLOUDSCRIBE-WINDOWS.cmd' = '07805f5bf94f03f5130b5fd76f946d87994b68e34cbd56b629421575863888bb'
    'BUILDING-WINDOWS.txt' = '17ada2280a4ebba4b46f70462e54737ca1c09a162f75ceff7fad9ef33dd2175f'
    'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs' = '3c6ff20532982f54dbb92ce97b8dddbbeefa9a1154643a4f6415c59c35aaee6b'
    'tools/run_python_regression_shards.py' = 'b667dd6896ca4f82e7b17e46a8af6d998e8699169368b92552354e57e8824925'
    'SESSION_STATE.json' = '6f72a29066b5901707dc1e0b829b98bcbf71829e7ea376635949e1c57e729b1e'
}
foreach ($relative in $expected.Keys) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $SourceRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$relative]) { throw "Build-launcher overlay hash mismatch for ${relative}: $actual" }
}

Write-Host 'CLOUDSCRIBE_WINDOWS_BUILD_LAUNCHER_OVERLAY=PASS'
