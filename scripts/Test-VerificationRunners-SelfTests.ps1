#requires -Version 7.0
[CmdletBinding()]
param([string]$DotnetPath = 'dotnet', [string]$PythonPath = 'python')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verification/Verification.Common.ps1')
. (Join-Path $PSScriptRoot 'verification/NativeEvidence.Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-runner-selftests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$script:assertions = 0
$skips = @()
function Assert-True([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Assertion failed: $Description" }
    $script:assertions++
}
function Expect-Rejection([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-True ($message -match $Pattern) "Expected rejection $Pattern, received '$message'"
}
function New-FixtureFile([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content)
    return @{ path = [IO.Path]::GetFileName($Path); bytes = (Get-Item -LiteralPath $Path).Length; sha256 = (Get-FileHash -LiteralPath $Path).Hash }
}
function Invoke-TestNative([hashtable]$Configuration, [string]$Label, [string]$ProcessMock = 'function Get-Process { @() }; ') {
    $configPath = Join-Path $root "$Label.clixml"
    $Configuration | Export-Clixml -LiteralPath $configPath
    # Mock only process discovery in a child: fixtures are inert bytes, never launched.
    $command = $ProcessMock +
        '$config = Import-Clixml -LiteralPath ''' + $configPath.Replace("'", "''") + "'; & '" +
        (Join-Path $PSScriptRoot 'Invoke-NativeAcceptanceEvidence.ps1').Replace("'", "''") + "' @config; exit " + '$LASTEXITCODE'
    $code = Invoke-LoggedProcess $pwsh @('-NoProfile', '-Command', $command) $repo (Join-Path $root $Label)
    return @{ code = $code; report = (Get-Content -LiteralPath (Join-Path $Configuration.OutputDirectory 'summary.json') -Raw | ConvertFrom-Json) }
}
try {
    Assert-True (Test-PathOverlap $root (Join-Path $root 'child')) 'Child overlaps parent'
    Assert-True (-not (Test-PathOverlap $root ($root + '-sibling'))) 'Common name prefix is not overlap'
    Expect-Rejection { Resolve-PhysicalPath 'relative' } 'absolute'
    Expect-Rejection { New-EvidenceDirectory $root @() } 'must be new'
    Expect-Rejection { New-EvidenceDirectory (Join-Path $root 'bad-output') @($root) } 'overlaps'
    Expect-Rejection { Resolve-ManifestMember $root '../outside' } 'Unsafe'
    Expect-Rejection { Resolve-ManifestMember $root 'C:\outside' } 'Unsafe'

    $stages = @(
        (New-CheckStage 'nonzero' $pwsh @('-NoProfile', '-Command', '[Console]::Error.WriteLine("failure retained"); exit 7') $root),
        (New-CheckStage 'dependent' $pwsh @('-NoProfile', '-Command', 'throw "must not execute"') $root @('nonzero')),
        (New-CheckStage 'missing-dependency' $pwsh @('-NoProfile', '-Command', 'exit 3') $root -Prerequisite),
        (New-CheckStage 'deferred' $pwsh @('-NoProfile', '-Command', 'Write-Output "validation deferred"; exit 0') $root @() 'complete validator PASS'),
        (New-CheckStage 'success' $pwsh @('-NoProfile', '-Command', 'Write-Output "Skipped: synthetic unsupported fixture"; Write-Output "passed: 1"; exit 0') $root)
    )
    Invoke-CheckStages $stages $root {}
    Assert-True ($stages[0].status -eq 'FAIL' -and $stages[0].exit_code -eq 7) 'Nonzero child exit is never swallowed'
    Assert-True ($stages[1].status -eq 'BLOCKED' -and $null -eq $stages[1].started_utc) 'Dependent command was not started'
    Assert-True ($stages[2].status -eq 'BLOCKED') 'Unavailable dependency is blocked'
    Assert-True ($stages[3].status -eq 'BLOCKED') 'Deferred zero exit cannot certify schema validation'
    Assert-True ($stages[4].status -eq 'PASS' -and $stages[4].skip_lines.Count -eq 1) 'Independent success and exact skip evidence retained'
    Assert-True ((Get-Content (Join-Path $root 'nonzero.stderr.log') -Raw).Contains('failure retained')) 'Stderr retained'
    Assert-True ((Get-EvidenceExitCode $stages) -eq 1) 'Failure exit takes priority'
    Assert-True ((Get-EvidenceExitCode @((New-NativeCase 'pending' 'W08' 'Pending'))) -eq 2) 'Unobserved case is incomplete'
    Assert-True ((Get-EvidenceExitCode @($stages[4])) -eq 0) 'Only completed command returns zero'

    $app = Join-Path $root 'candidate'
    $profile = Join-Path $root 'CloudScribe-acceptance-synthetic'
    $fixtures = Join-Path $root 'CloudScribe-fixture-synthetic'
    $protected = Join-Path $root 'real-profile-placeholder'
    foreach ($path in @($app, (Join-Path $app 'app'), (Join-Path $app 'runtime'), $profile, $fixtures, $protected)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
    $commit = 'a' * 40
    $packagePath = Join-Path $root 'package.json'
    $exeMember = New-FixtureFile (Join-Path $app 'app/CloudScribe.exe') 'inert synthetic bytes, never executable'
    $exeMember.path = 'app/CloudScribe.exe'
    $runtimeMember = New-FixtureFile (Join-Path $app 'runtime/coreclr.dll') 'inert runtime fixture, never loaded'
    $runtimeMember.path = 'runtime/coreclr.dll'
    Write-EvidenceJson $packagePath @{ schema_version = 1; candidate_commit = $commit; files = @($exeMember, $runtimeMember) }
    $fixturePath = Join-Path $root 'fixture.json'
    $source = New-FixtureFile (Join-Path $fixtures 'source.txt') 'Synthetic source, no private text.'
    $source.kind = 'source'
    Write-EvidenceJson $fixturePath @{ schema_version = 1; files = @($source) }
    $fixtureHash = (Get-FileHash $fixturePath).Hash
    Write-EvidenceJson (Join-Path $profile '.cloudscribe-synthetic-fixture.json') @{
        schema_version = 1; synthetic = $true; disconnected = $true; contains_private_data = $false
        profile_path = $profile; fixture_path = $fixtures; fixture_manifest_sha256 = $fixtureHash
    }
    New-FixtureFile (Join-Path $profile 'library.synthetic') 'synthetic state' | Out-Null
    $config = @{
        Phase = 'Before'; CandidateDirectory = $app; CandidateCommit = $commit
        PackageManifest = $packagePath; PackageManifestSha256 = (Get-FileHash $packagePath).Hash
        ProfileDirectory = $profile; FixtureDirectory = $fixtures; FixtureManifest = $fixturePath
        FixtureManifestSha256 = $fixtureHash; ProtectedRoots = @($protected)
        OutputDirectory = (Join-Path $root 'before')
    }
    $before = Invoke-TestNative $config 'before-child'
    Assert-True ($before.code -eq 2) 'Native script incomplete gates exit two, not false green'
    Assert-True (($before.report.cases | Where-Object case_id -EQ 'closed-snapshot').status -eq 'PASS') 'Synthetic closed snapshot executed'
    Assert-True (@($before.report.cases | Where-Object { $_.lane -ne 'script' -and $_.status -ne 'NOT RUN' }).Count -eq 0) 'Every actual observation remains NOT RUN'
    Assert-True ($null -eq $before.report.paid_submission_count) 'Uninstrumented app submissions remain unknown'
    $processConfig = $config.Clone()
    $processConfig.Phase = 'Process'
    $processConfig.AppProcessId = 123
    $processConfig.OutputDirectory = Join-Path $root 'process-sibling-layout'
    $mockModules = @'
function Get-Process {
    $exe = Join-Path $config.CandidateDirectory 'app/CloudScribe.exe'
    $runtime = Join-Path $config.CandidateDirectory 'runtime/coreclr.dll'
    $version = [pscustomobject]@{ FileVersion = 'synthetic-test-runtime' }
    $modules = @(
        [pscustomobject]@{ FileName = $exe; ModuleName = 'CloudScribe.exe'; FileVersionInfo = $version },
        [pscustomobject]@{ FileName = $runtime; ModuleName = 'coreclr.dll'; FileVersionInfo = $version }
    )
    return [pscustomobject]@{ MainModule = $modules[0]; Modules = $modules; HasExited = $false }
};
'@
    $processResult = Invoke-TestNative $processConfig 'process-sibling-child' $mockModules
    Assert-True (($processResult.report.cases | Where-Object case_id -EQ 'loaded-modules').status -eq 'PASS') 'Pinned app/runtime sibling layout supports loaded module evidence'
    Assert-True ($processResult.report.module_hashes.Count -eq 2) 'Observed executable and runtime hashes retained'
    New-FixtureFile (Join-Path $protected 'coreclr.dll') 'outside runtime fixture' | Out-Null
    $outsideMock = $mockModules.Replace("Join-Path "+'$config.CandidateDirectory'+" 'runtime/coreclr.dll'", "Join-Path ("+'$config.ProtectedRoots[0]'+") 'coreclr.dll'")
    $processConfig.OutputDirectory = Join-Path $root 'process-outside-runtime'
    $outside = Invoke-TestNative $processConfig 'process-outside-runtime-child' $outsideMock
    Assert-True (($outside.report.cases | Where-Object case_id -EQ 'loaded-modules').status -eq 'BLOCKED') 'Unpinned outside runtime never passes'
    $shortcutPath = Join-Path $root 'synthetic.lnk'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try { $shortcut.TargetPath = Join-Path $app 'app/CloudScribe.exe'; $shortcut.Save() }
        finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
    $shortcutHash = (Get-FileHash $shortcutPath).Hash
    $processConfig.ShortcutPath = $shortcutPath
    $processConfig.OutputDirectory = Join-Path $root 'process-shortcut'
    $shortcutResult = Invoke-TestNative $processConfig 'process-shortcut-child' $mockModules
    Assert-True (($shortcutResult.report.cases | Where-Object case_id -EQ 'shortcut-target').status -eq 'PASS') 'Existing synthetic shortcut target inspected'
    Assert-True ((Get-FileHash $shortcutPath).Hash -eq $shortcutHash) 'Shortcut remains byte-identical; never saved or launched by collector'
    Assert-True (($shortcutResult.report.cases | Where-Object case_id -EQ 'W02-shortcut-launch').status -eq 'NOT RUN') 'Reading a shortcut never claims launch'
    $processConfig.ExecutableRelativePath = '../outside.exe'
    $processConfig.OutputDirectory = Join-Path $root 'process-traversal'
    $traversalResult = Invoke-TestNative $processConfig 'process-traversal-child' $mockModules
    Assert-True (($traversalResult.report.cases | Where-Object case_id -EQ 'loaded-modules').status -eq 'BLOCKED') 'Executable traversal cannot obtain process evidence'
    $config.Phase = 'After'
    $config.OutputDirectory = Join-Path $root 'after'
    $config.BeforeEvidence = Join-Path $root 'before/snapshot.json'
    $config.BeforeEvidenceSha256 = (Get-FileHash $config.BeforeEvidence).Hash
    $after = Invoke-TestNative $config 'after-child'
    Assert-True (($after.report.cases | Where-Object case_id -EQ 'byte-preservation').status -eq 'PASS') 'Unchanged pinned snapshot compares equal'
    [IO.File]::AppendAllText((Join-Path $profile 'library.synthetic'), ' changed')
    $config.OutputDirectory = Join-Path $root 'changed'
    $changed = Invoke-TestNative $config 'changed-child'
    Assert-True ($changed.code -eq 1 -and ($changed.report.cases | Where-Object case_id -EQ 'byte-preservation').status -eq 'FAIL') 'Changed bytes produce FAIL and nonzero'
    $config.PackageManifestSha256 = '0' * 64
    $config.OutputDirectory = Join-Path $root 'bad-hash'
    $bad = Invoke-TestNative $config 'bad-hash-child'
    Assert-True ($bad.code -eq 2 -and $bad.report.preflight -eq 'BLOCKED') 'Tampered pinned input is blocked'


    # Exercise already-installed media tools using a generated nonprivate tone, never a provider.
    $ffmpegCommand = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $ffprobeCommand = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ffmpegCommand -and $ffprobeCommand) {
        $audioPath = Join-Path $fixtures 'tone.mp3'
        $code = Invoke-LoggedProcess $ffmpegCommand.Source @('-nostdin', '-v', 'error', '-f', 'lavfi',
            '-i', 'sine=frequency=440:duration=0.5', '-c:a', 'libmp3lame', $audioPath) $root (Join-Path $root 'generate-tone')
        Assert-True ($code -eq 0) 'Local synthetic tone fixture generated'
        $audioMember = @{ path = 'tone.mp3'; bytes = (Get-Item $audioPath).Length; sha256 = (Get-FileHash $audioPath).Hash
            kind = 'audio'; provenance_id = 'selftest-local-tone'; permitted_scope = 'nonprivate-local-acceptance'
            minimum_duration_seconds = 0.4; maximum_duration_seconds = 1.0 }
        Write-EvidenceJson $fixturePath @{ schema_version = 1; files = @($source, $audioMember) }
        $config.FixtureManifestSha256 = (Get-FileHash $fixturePath).Hash
        $markerPath = Join-Path $profile '.cloudscribe-synthetic-fixture.json'
        $marker = Get-Content $markerPath -Raw | ConvertFrom-Json -AsHashtable
        $marker.fixture_manifest_sha256 = $config.FixtureManifestSha256
        Write-EvidenceJson $markerPath $marker
        $config.Phase = 'Before'
        $config.PackageManifestSha256 = (Get-FileHash $packagePath).Hash
        $config.OutputDirectory = Join-Path $root 'media-valid'
        $config.FfprobePath = $ffprobeCommand.Source
        $config.FfmpegPath = $ffmpegCommand.Source
        $media = Invoke-TestNative $config 'media-valid-child'
        Assert-True (($media.report.cases | Where-Object case_id -EQ 'audio-1').status -eq 'PASS') 'Actual local MP3 metadata and complete decode verified'
        Assert-True (@($media.report.cases | Where-Object { $_.lane -eq 'listening' -and $_.status -ne 'NOT RUN' }).Count -eq 0) 'Decoding does not claim listening'
        $config.FfmpegPath = ''
        $config.OutputDirectory = Join-Path $root 'media-no-decoder'
        $noDecoder = Invoke-TestNative $config 'media-no-decoder-child'
        Assert-True (($noDecoder.report.cases | Where-Object case_id -EQ 'audio-1').status -eq 'BLOCKED') 'Missing decoder explicitly blocked'
        $config.FfmpegPath = $ffmpegCommand.Source
        [IO.File]::WriteAllText($audioPath, 'This is not an MP3.')
        $audioMember.bytes = (Get-Item $audioPath).Length
        $audioMember.sha256 = (Get-FileHash $audioPath).Hash
        Write-EvidenceJson $fixturePath @{ schema_version = 1; files = @($source, $audioMember) }
        $config.FixtureManifestSha256 = (Get-FileHash $fixturePath).Hash
        $marker.fixture_manifest_sha256 = $config.FixtureManifestSha256
        Write-EvidenceJson $markerPath $marker
        $config.OutputDirectory = Join-Path $root 'media-corrupt'
        $corrupt = Invoke-TestNative $config 'media-corrupt-child'
        Assert-True ($corrupt.code -eq 1 -and ($corrupt.report.cases | Where-Object case_id -EQ 'audio-1').status -eq 'FAIL') 'Corrupt declared audio is FAIL, not a playable-output pass'
    } else {
        $skips += 'ffmpeg/ffprobe unavailable: actual local MP3 success/error checks NOT RUN; no tools installed.'
    }

    # Snapshot unit tests mock process discovery only; actual filesystem locking is exercised.
    function Assert-AppClosed {}
    $lockPath = Join-Path $profile 'library.synthetic'
    $lock = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
    try { Expect-Rejection { Get-ClosedFixtureSnapshot $profile } 'used by another process|being used|sharing|process cannot access' }
    finally { $lock.Dispose() }
    New-FixtureFile (Join-Path $profile 'test.db-wal') 'pending WAL' | Out-Null
    Expect-Rejection { Get-ClosedFixtureSnapshot $profile } 'sidecar'
    $link = Join-Path $root 'junction'
    try {
        New-Item -ItemType Junction -Path $link -Target $fixtures -ErrorAction Stop | Out-Null
        Expect-Rejection { Resolve-PhysicalPath $link } 'Reparse'
    } catch {
        if ($_.Exception.Message -like 'Assertion failed:*') { throw }
        $skips += 'Junction creation unavailable; real reparse rejection fixture NOT RUN: ' + $_.Exception.Message
    }

    $planOut = Join-Path $root 'developer-plan'
    $head = (& git -C $repo -c core.bare=false rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify self-test checkout.' }
    $planCode = Invoke-LoggedProcess $pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Invoke-DeveloperChecks.ps1'),
        '-OutputDirectory', $planOut, '-ExpectedCommit', $head, '-Repository', $repo,
        '-DotnetPath', $DotnetPath, '-PythonPath', $PythonPath, '-PlanOnly') $repo (Join-Path $root 'developer-plan-child')
    $plan = Get-Content -LiteralPath (Join-Path $planOut 'summary.json') -Raw | ConvertFrom-Json
    Assert-True ($planCode -eq 2 -and $plan.preflight -eq 'PASS') 'Plan-only returns incomplete with real tool identities'
    Assert-True (@($plan.stages | Where-Object status -NE 'NOT RUN').Count -eq 0) 'Plan-only executes no suite stages'
    Assert-True (($plan.stages | Where-Object id -EQ 'full-mtp-solution').arguments -contains '--solution') 'MTP solution syntax retained'
    Assert-True (($plan.stages | Where-Object id -EQ 'full-mtp-solution').depends_on -contains 'strict-release-build') 'No stale no-build run without successful build'
    Assert-True (@($plan.stages | Where-Object id -EQ 'verification-runner-self-tests').Count -eq 1) 'Self-tests named once, not recursively executed by plan'
    Assert-True (@($plan.stages | Where-Object id -EQ 'complete-control-schema').Count -eq 1) 'Executable schema validation separately represented'
    $environmentOut = Join-Path $root 'developer-environment'
    $environmentCommand = '$savedPath = $env:PATH; $env:DOTNET_ROOT = "restore-root-sentinel"; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "restore-cert-sentinel"; & ' +
        "'" + (Join-Path $PSScriptRoot 'Invoke-DeveloperChecks.ps1').Replace("'", "''") + "' " +
        "-OutputDirectory '" + $environmentOut.Replace("'", "''") + "' -ExpectedCommit '$head' -Repository '" + $repo.Replace("'", "''") +
        "' -DotnetPath '" + $DotnetPath.Replace("'", "''") + "' -PythonPath '" + $PythonPath.Replace("'", "''") + "' -PlanOnly; " +
        '$result = $LASTEXITCODE; if ($env:PATH -ne $savedPath -or $env:DOTNET_ROOT -ne "restore-root-sentinel" -or $env:DOTNET_GENERATE_ASPNET_CERTIFICATE -ne "restore-cert-sentinel") { exit 41 }; exit $result'
    $environmentCode = Invoke-LoggedProcess $pwsh @('-NoProfile', '-Command', $environmentCommand) $repo (Join-Path $root 'environment-child')
    $environmentReport = Get-Content (Join-Path $environmentOut 'summary.json') -Raw | ConvertFrom-Json
    Assert-True ($environmentCode -eq 2) 'DOTNET_ROOT and certificate environment restored after runner'
    Assert-True ($environmentReport.process_environment.DOTNET_ROOT -eq (Split-Path $environmentReport.tools.dotnet.path)) 'Selected SDK directory is the child DOTNET_ROOT'
    Assert-True ($environmentReport.process_environment.DOTNET_GENERATE_ASPNET_CERTIFICATE -eq 'false') 'No development certificate generation requested'
    $blockedEnvironmentOut = Join-Path $root 'developer-environment-blocked'
    $blockedEnvironmentCommand = $environmentCommand.Replace($environmentOut, $blockedEnvironmentOut).Replace("-ExpectedCommit '$head'", ("-ExpectedCommit '" + ('0' * 40) + "'"))
    $blockedEnvironmentCode = Invoke-LoggedProcess $pwsh @('-NoProfile', '-Command', $blockedEnvironmentCommand) $repo (Join-Path $root 'environment-blocked-child')
    Assert-True ($blockedEnvironmentCode -eq 2) 'Environment also restored after blocked preflight'
    Write-ArtifactManifest $root
    Assert-True (Test-Path (Join-Path $root 'artifacts.json')) 'Artifact inventory written'
    Write-Output "PASS: $script:assertions assertions. Skipped fixtures: $($skips.Count). Evidence retained: $root"
    foreach ($skip in $skips) { Write-Output "SKIPPED: $skip" }
} catch {
    Write-Error "Self-test failure. Fixtures and logs retained at $root. $($_.Exception.Message)"
    exit 1
}
