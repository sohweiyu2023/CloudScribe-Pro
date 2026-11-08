#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Before', 'After', 'Process')][string]$Phase,
    [Parameter(Mandatory)][Alias('CandidateDirectory')][string]$PackageDirectory,
    [string]$ExecutableRelativePath = 'app/CloudScribe.exe',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CandidateCommit,
    [Parameter(Mandatory)][string]$PackageManifest,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PackageManifestSha256,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$FixtureManifest,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$FixtureManifestSha256,
    [Parameter(Mandatory)][string[]]$ProtectedRoots,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BeforeEvidence = '',
    [string]$BeforeEvidenceSha256 = '',
    [int]$AppProcessId = 0,
    [string]$ShortcutPath = '',
    [string]$FfprobePath = '',
    [string]$FfmpegPath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verification/Verification.Common.ps1')
. (Join-Path $PSScriptRoot 'verification/NativeEvidence.Common.ps1')
$repo = Resolve-PhysicalPath (Split-Path -Parent $PSScriptRoot)
# Resolve and reject overlaps before any output or profile inspection.
$app = Resolve-PhysicalPath $PackageDirectory
$profile = Resolve-PhysicalPath $ProfileDirectory
$fixtures = Resolve-PhysicalPath $FixtureDirectory
$roots = @($app, $profile, $fixtures, [IO.Path]::GetFullPath($OutputDirectory))
for ($i = 0; $i -lt $roots.Count; $i++) {
    foreach ($other in $roots | Select-Object -Skip ($i + 1)) {
        if (Test-PathOverlap $roots[$i] $other) { throw 'Candidate, profile, fixtures and output must be disjoint.' }
    }
}
$protected = @($repo) + @($ProtectedRoots | ForEach-Object { Resolve-PhysicalPath $_ })
# The caller must list every real profile, audio/library and backup root, including custom overrides.
foreach ($root in @($profile, $fixtures, $OutputDirectory)) {
    foreach ($deny in $protected) {
        if (Test-PathOverlap $root $deny) { throw "Disposable/evidence root overlaps protected root: $deny" }
    }
    foreach ($alias in @($HOME, $env:USERPROFILE, $env:WINDIR, $env:APPDATA, $env:LOCALAPPDATA,
        $env:ProgramFiles, [Environment]::GetFolderPath('MyDocuments'))) {
        if ($alias -and [IO.Path]::GetFullPath($root).TrimEnd('\') -eq [IO.Path]::GetFullPath($alias).TrimEnd('\')) {
            throw 'System, user-profile and document-root aliases are forbidden.'
        }
    }
}
if ((Split-Path $profile -Leaf) -notlike 'CloudScribe-acceptance-*' -or
    (Split-Path $fixtures -Leaf) -notlike 'CloudScribe-fixture-*') {
    throw 'Use explicitly named CloudScribe-acceptance-* and CloudScribe-fixture-* disposable directories.'
}
$out = New-EvidenceDirectory $OutputDirectory (@($repo, $app, $profile, $fixtures) + $protected)
$report = [ordered]@{
    schema_version = 1; kind = 'native-acceptance-evidence'; phase = $Phase
    candidate_commit = $CandidateCommit; candidate_commit_source = 'Build-owner assertion bound to pinned package manifest.'
    package_root = $app; executable_relative_path = $ExecutableRelativePath
    executable_path = (Join-Path $app $ExecutableRelativePath); executable_sha256 = $null
    module_hashes = @(); runtime_version = $null; process_id = $AppProcessId
    process_profile_binding = 'NOT VERIFIED: process inspection does not establish which profile that process uses.'
    profile_kind = 'disposable synthetic inputs only'; profile_path = $profile; fixture_path = $fixtures
    fixture_manifest_sha256 = $FixtureManifestSha256; package_manifest_sha256 = $PackageManifestSha256
    before_manifest_sha256 = $null; after_manifest_sha256 = $null
    started_utc = [DateTime]::UtcNow.ToString('o'); finished_utc = $null
    automation_script_revision = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    command_or_steps = 'Read-only evidence capture; no app launch, foreground input, provider call or cleanup.'
    paid_submission_count = $null; credential_mutation_count = $null
    observation_method = 'This runner has no provider/vault operations. App activity is uninstrumented, not presumed zero.'
    settings_before = @{}; settings_after = @{}; restoration_status = 'NOT APPLICABLE: no settings changed by runner.'
    limitations = @('No real-profile inspection, SQLite migration/read, app launch, install, UI automation or listening.',
        'Byte manifests prove file identity, not semantic queue/authorization state or provider inactivity.',
        'Supplied audio provenance is a build-owner assertion, not independent provider/payload verification.',
        'Process module hashes describe observed loaded paths, not how the process was launched.',
        'No observation can pass through this runner; complete the separate observation worksheet with evidence.')
    cases = @(); family_summary = @(); snapshot = $null; preflight = 'NOT RUN'; exit_code = 2
}
function Add-Case([string]$Id, [string]$Family, [string]$Scenario, [string]$Lane = 'script') {
    $case = New-NativeCase $Id $Family $Scenario $Lane
    $report.cases += $case
    return $case
}
$packageCase = Add-Case 'package-files' 'W02' 'Pinned package members match SHA-256 and length.'
$snapshotCase = Add-Case 'closed-snapshot' 'W03' 'Closed synthetic profile and source/audio fixtures captured under exclusive read locks.'
$preservationCase = Add-Case 'byte-preservation' 'W03' 'After capture is byte-identical to pinned before snapshot; no intended mutations assumed.'
$moduleCase = Add-Case 'loaded-modules' 'W02' 'Selected executable and every declared critical app/runtime module are observed loaded and match pinned members.'
$shortcutCase = Add-Case 'shortcut-target' 'W02' 'Actual supplied shortcut targets the exact candidate executable.'
$activityCase = Add-Case 'provider-counters' 'B01' 'Instrumented app provider submissions and credential mutations are zero.'
$activityCase.status = 'BLOCKED'; $activityCase.reason = 'No supported app counter instrumentation supplied; absence of logs is not proof.'
$databaseCase = Add-Case 'logical-database-state' 'Q09' 'Library, queue and authorization identities preserve expected logical state.'
$databaseCase.status = 'BLOCKED'; $databaseCase.reason = 'Only closed byte preservation is implemented; no schema-aware database adapter.'
foreach ($spec in @(
    @('W02-shortcut-launch', 'W02', 'Launch via the actual supplied shortcut and observe displayed version', 'observed_ui'),
    @('V02-source-voice-linkage', 'V02', 'Exact Neural2-D/Aoede source, payload, voice and complete-text comparison', 'listening'),
    @('V10-beginning-ending', 'V10', 'Listen to beginning and ending with timestamps', 'listening'),
    @('V10-every-join', 'V10', 'Listen at every declared chunk join', 'listening'),
    @('V10-difficult-passages', 'V10', 'Listen to difficult pronunciation passages', 'listening'),
    @('V10-full-coverage', 'V10', 'Full listening or validated alignment for omission/repetition', 'listening'),
    @('Q12-play-pause-seek', 'Q12', 'Play/pause and boundary/rapid seek agree with audible timeline', 'observed_ui'),
    @('Q12-previous-next-eof', 'Q12', 'Previous/next and end-of-file preserve selected track', 'observed_ui'),
    @('Q12-close-reopen', 'Q12', 'Close/reopen preserves verified fixture and accessible state', 'observed_ui'),
    @('W07-voices-focus', 'W07', 'Keyboard Voices navigation and dismiss return focus', 'observed_ui'),
    @('W07-populated-batch', 'W07', 'Keyboard populated Batch navigation and dismiss return focus', 'observed_ui'),
    @('W07-delayed-response', 'W07', 'Delayed fake catalog and repeated keys never submit or approve', 'observed_ui'),
    @('W08-accessibility-tree', 'W08', 'Inspect actual labels, roles, states and order', 'observed_ui'),
    @('W08-announcements', 'W08', 'Actual screen reader announces voice, totals, errors, playback and focus', 'screen_reader'),
    @('W09-small-window', 'W09', 'Actual small and normal windows have reachable unclipped actions', 'observed_ui'),
    @('W09-long-labels', 'W09', 'Actual long localized labels remain readable', 'observed_ui'),
    @('W09-high-contrast', 'W09', 'Actual authorized high contrast remains usable', 'observed_ui'),
    @('W09-dpi-100', 'W09', 'Actual 100 percent scale checked', 'observed_ui'),
    @('W09-dpi-125', 'W09', 'Actual 125 percent scale checked', 'observed_ui'),
    @('W09-dpi-150', 'W09', 'Actual 150 percent scale checked', 'observed_ui'),
    @('W09-dpi-200', 'W09', 'Actual 200 percent scale checked', 'observed_ui'))) {
    Add-Case $spec[0] $spec[1] $spec[2] $spec[3] | Out-Null
}
function Save-NativeReport { Write-EvidenceJson (Join-Path $out 'summary.json') $report }
try {
    foreach ($input in @(@($PackageManifest, $PackageManifestSha256), @($FixtureManifest, $FixtureManifestSha256))) {
        $path = Resolve-PhysicalPath $input[0]
        if ((Get-FileHash -LiteralPath $path).Hash -ne $input[1]) { throw 'Pinned input manifest hash mismatch.' }
    }
    $package = Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json
    $fixture = Get-Content -LiteralPath $FixtureManifest -Raw | ConvertFrom-Json
    if ($package.candidate_commit -ne $CandidateCommit) { throw 'Package manifest commit mismatch.' }
    $markerPath = Resolve-PhysicalPath (Join-Path $profile '.cloudscribe-synthetic-fixture.json')
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.schema_version -ne 1 -or $marker.synthetic -cne $true -or $marker.disconnected -cne $true -or
        $marker.contains_private_data -cne $false -or $marker.profile_path -ne $profile -or
        $marker.fixture_path -ne $fixtures -or $marker.fixture_manifest_sha256 -ne $FixtureManifestSha256) {
        throw 'Disposable nonprivate/disconnected fixture declaration does not bind these inputs.'
    }
    Test-HashedManifest $fixtures $fixture
    $report.preflight = 'PASS'
    Invoke-NativeCase $packageCase {
        try { Test-HashedManifest $app $package }
        catch { if ($_.Exception.Message -like 'Manifest identity mismatch:*') { throw ('FAIL: ' + $_.Exception.Message) }; throw }
        $report.executable_path = Resolve-ManifestMember $app $ExecutableRelativePath
        if ([IO.Path]::GetFileName($report.executable_path) -ine 'CloudScribe.exe') { throw 'Executable must be named CloudScribe.exe for the closed-process guard.' }
        $packageIndex = @{}
        foreach ($member in $package.files) { $packageIndex[(Resolve-ManifestMember $app $member.path)] = $member }
        if (-not $packageIndex.ContainsKey($report.executable_path)) { throw 'Package manifest omits the selected executable.' }
        $report.executable_sha256 = (Get-FileHash -LiteralPath $report.executable_path).Hash
        return 'Pinned package members verified. This alone is not evidence of loaded modules.'
    }
    Save-NativeReport
    if ($Phase -in @('Before', 'After')) {
        Invoke-NativeCase $snapshotCase {
            $report.snapshot = [ordered]@{
                candidate_commit = $CandidateCommit; profile_path = $profile; fixture_path = $fixtures
                fixture_manifest_sha256 = $FixtureManifestSha256
                profile_files = @(Get-ClosedFixtureSnapshot $profile)
                fixture_files = @(Get-ClosedFixtureSnapshot $fixtures)
            }
            $snapshotPath = Join-Path $out 'snapshot.json'
            Write-EvidenceJson $snapshotPath $report.snapshot
            $hash = (Get-FileHash -LiteralPath $snapshotPath).Hash
            if ($Phase -eq 'Before') { $report.before_manifest_sha256 = $hash } else { $report.after_manifest_sha256 = $hash }
            return @{ snapshot_sha256 = $hash; file_count = $report.snapshot.profile_files.Count + $report.snapshot.fixture_files.Count }
        }
        if ($Phase -eq 'After') {
            Invoke-NativeCase $preservationCase {
                if ($snapshotCase.status -ne 'PASS') { throw 'Closed snapshot prerequisite unavailable.' }
                if (-not $BeforeEvidence -or $BeforeEvidenceSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Pinned before snapshot is required for After.' }
                $beforePath = Resolve-PhysicalPath $BeforeEvidence
                if ((Get-FileHash -LiteralPath $beforePath).Hash -ne $BeforeEvidenceSha256) { throw 'Before snapshot hash mismatch.' }
                $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json -AsHashtable
                if (($before | ConvertTo-Json -Depth 20 -Compress) -cne ($report.snapshot | ConvertTo-Json -Depth 20 -Compress)) {
                    throw 'FAIL: Closed profile/fixture bytes or bound identity changed. Inspect independently; no mutation is implicitly accepted.'
                }
                $report.before_manifest_sha256 = $BeforeEvidenceSha256
                return 'All captured file paths, lengths and hashes match the pinned before snapshot.'
            }
        }
    }
    if ($Phase -eq 'Process') {
        Invoke-NativeCase $moduleCase {
            if ($packageCase.status -ne 'PASS' -or $AppProcessId -le 0) { throw 'Verified package and explicit existing process ID required.' }
            if (-not $package.PSObject.Properties['required_loaded_modules'] -or -not @($package.required_loaded_modules).Count) {
                throw 'Pinned manifest must declare required_loaded_modules for the critical app/runtime set.'
            }
            $packageMembers = @{}
            foreach ($member in $package.files) { $packageMembers[(Resolve-ManifestMember $app $member.path)] = $member }
            $requiredModules = @{}
            foreach ($relativePath in $package.required_loaded_modules) {
                $requiredPath = Resolve-ManifestMember $app $relativePath
                if (-not $packageMembers.ContainsKey($requiredPath) -or $requiredModules.ContainsKey($requiredPath)) {
                    throw 'Required loaded module is unpinned or duplicated.'
                }
                $requiredModules[$requiredPath] = [IO.Path]::GetFileName($requiredPath)
            }
            $criticalNames = @('CloudScribe.exe', 'CloudScribe.dll', 'CloudScribe.Infrastructure.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')
            foreach ($name in $criticalNames) {
                if (@($requiredModules.Values | Where-Object { $_ -ieq $name }).Count -ne 1) {
                    throw "Pinned required module list must identify exactly one $name."
                }
            }
            $process = Get-Process -Id $AppProcessId -ErrorAction Stop
            if ($process.MainModule.FileName -ne $report.executable_path) { throw 'Selected process is not the exact candidate executable.' }
            $modules = @($process.Modules)
            $packageModules = @()
            $observedModules = @{}
            foreach ($module in $modules) {
                $modulePath = Resolve-PhysicalPath $module.FileName
                $entry = @{ path = $modulePath; sha256 = (Get-FileHash -LiteralPath $modulePath).Hash; version = $module.FileVersionInfo.FileVersion }
                $report.module_hashes += $entry
                $observedModules[$modulePath] = $entry
                if ($modulePath.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    $relative = [IO.Path]::GetRelativePath($app, $modulePath)
                    if (-not $packageMembers.ContainsKey($modulePath) -or $packageMembers[$modulePath].sha256 -ne $entry.sha256) {
                        throw "FAIL: Loaded package module mismatch: $relative"
                    }
                    $packageModules += $relative
                }
                if ($module.ModuleName -ieq 'coreclr.dll') { $report.runtime_version = $entry.version }
            }
            if (@($modules | Where-Object { $_.ModuleName -in $criticalNames -and -not $_.FileName.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
                throw 'Critical app/runtime module is outside the pinned package; observed hashes do not establish expected identity.'
            }
            $missingModules = @($requiredModules.Keys | Where-Object { -not $observedModules.ContainsKey($_) })
            if ($missingModules.Count) {
                throw ('Required loaded modules not observed: ' + (($missingModules | ForEach-Object { [IO.Path]::GetRelativePath($app, $_) }) -join ', '))
            }
            if (-not $report.runtime_version -or -not $packageModules.Count) { throw 'Runtime or package module enumeration incomplete.' }
            if ($process.HasExited) { throw 'Process exited during inspection.' }
            return @{ matched_package_modules = $packageModules; runtime = $report.runtime_version }
        }
    }
    if ($ShortcutPath) {
        Invoke-NativeCase $shortcutCase {
            if (-not $IsWindows) { throw 'Windows shortcut inspection unavailable.' }
            $shortcut = Resolve-PhysicalPath $ShortcutPath
            if ([IO.Path]::GetExtension($shortcut) -ine '.lnk') { throw 'Expected an existing .lnk file.' }
            $shell = New-Object -ComObject WScript.Shell
            try {
                $link = $shell.CreateShortcut($shortcut)
                try {
                    if ($link.TargetPath -ne $report.executable_path) { throw 'FAIL: Shortcut targets a different executable.' }
                    return @{ path = $shortcut; sha256 = (Get-FileHash -LiteralPath $shortcut).Hash; target = $link.TargetPath
                        limitation = 'Target inspected only. Launch through this shortcut and displayed version remain NOT RUN.' }
                } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($link) | Out-Null }
            } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
        }
    }
    $audioMembers = @($fixture.files | Where-Object { $_.PSObject.Properties['kind'] -and $_.kind -eq 'audio' })
    if (-not $audioMembers.Count) {
        $audio = Add-Case 'audio-unavailable' 'V10' 'Verified local MP3 is decoded and metadata checked.'
        $audio.status = 'BLOCKED'; $audio.reason = 'No audio member in the pinned nonprivate fixture manifest.'
    }
    $index = 0
    foreach ($member in $audioMembers) {
        $index++
        $audio = Add-Case "audio-$index" 'V10' 'Pinned MP3 decodes completely and duration matches the declared interval.'
        Invoke-NativeCase $audio {
            if (-not $FfprobePath -or -not $FfmpegPath) { throw 'Explicit already-installed ffprobe and ffmpeg paths are required.' }
            $probe = Resolve-PhysicalPath $FfprobePath
            $decoder = Resolve-PhysicalPath $FfmpegPath
            $file = Resolve-ManifestMember $fixtures $member.path
            if (-not $member.provenance_id -or $member.permitted_scope -ne 'nonprivate-local-acceptance' -or
                $member.minimum_duration_seconds -le 0 -or $member.maximum_duration_seconds -lt $member.minimum_duration_seconds) {
                throw 'Audio provenance, nonprivate scope and positive expected duration interval required.'
            }
            $base = Join-Path $out "audio-$index-metadata"
            $code = Invoke-LoggedProcess $probe @('-v', 'error', '-protocol_whitelist', 'file', '-f', 'mp3', '-show_format', '-show_streams', '-of', 'json', $file) $out $base
            if ($code -ne 0) { throw "FAIL: ffprobe exited $code." }
            $metadata = Get-Content -LiteralPath ($base + '.stdout.log') -Raw | ConvertFrom-Json
            $streams = @($metadata.streams | Where-Object codec_type -EQ 'audio')
            $duration = [double]::Parse($metadata.format.duration, [Globalization.CultureInfo]::InvariantCulture)
            if ($streams.Count -ne 1 -or $streams[0].codec_name -ne 'mp3' -or
                [int]$streams[0].channels -le 0 -or [int]$streams[0].sample_rate -le 0 -or
                $duration -lt $member.minimum_duration_seconds -or $duration -gt $member.maximum_duration_seconds) {
                throw 'FAIL: MP3 stream metadata or declared duration interval mismatch.'
            }
            $code = Invoke-LoggedProcess $decoder @('-nostdin', '-v', 'error', '-xerror', '-protocol_whitelist', 'file', '-f', 'mp3', '-i', $file, '-map', '0:a:0', '-f', 'null', '-') $out (Join-Path $out "audio-$index-decode")
            if ($code -ne 0) { throw "FAIL: Complete decode exited $code." }
            if ((Get-FileHash -LiteralPath $file).Hash -ne $member.sha256) { throw 'FAIL: Audio changed during inspection.' }
            return @{ duration_seconds = $duration; channels = $streams[0].channels; sample_rate = $streams[0].sample_rate
                provenance_id = $member.provenance_id; ffprobe_path = $probe; ffprobe_sha256 = (Get-FileHash -LiteralPath $probe).Hash
                ffmpeg_path = $decoder; ffmpeg_sha256 = (Get-FileHash -LiteralPath $decoder).Hash
                limitation = 'Decode and container duration do not prove segment order, complete text, pronunciation or naturalness.' }
        }
        Save-NativeReport
    }
} catch {
    $report.preflight = 'BLOCKED'
    $report.limitations += $_.Exception.Message
    Write-Warning $_.Exception.Message
} finally {
    foreach ($case in $report.cases) {
        $names = @()
        if ($case.case_id -in @('closed-snapshot', 'byte-preservation')) { $names += 'snapshot.json' }
        if ($case.case_id -match '^audio-\d+$') {
            $names += "$($case.case_id)-metadata.stdout.log", "$($case.case_id)-metadata.stderr.log",
                "$($case.case_id)-decode.stdout.log", "$($case.case_id)-decode.stderr.log"
        }
        foreach ($name in $names) {
            $file = Join-Path $out $name
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                $case.evidence_files += @{ path = $name; kind = 'script-evidence'; sha256 = (Get-FileHash -LiteralPath $file).Hash
                    description = 'Raw evidence for the narrowly stated script assertion.' }
            }
        }
    }
    $report.family_summary = @($report.cases | Group-Object family_id, lane, status | ForEach-Object {
        @{ family_id = $_.Group[0].family_id; lane = $_.Group[0].lane; status = $_.Group[0].status; case_count = $_.Count }
    })
    $report.finished_utc = [DateTime]::UtcNow.ToString('o')
    $report.exit_code = Get-EvidenceExitCode $report.cases
    Save-NativeReport
    Write-ArtifactManifest $out
}
Write-Host "Evidence: $out (exit $($report.exit_code); observation gates remain separate)"
exit $report.exit_code
