# Reproducible developer checks and native evidence

These PowerShell 7 entry points supplement [DEVELOPMENT.md](DEVELOPMENT.md).
They do not install dependencies, configure credentials, make provider calls,
install CloudScribe, merge, publish, or change security settings. Restore may
contact the configured NuGet feeds. Keep all logs and fixtures outside every
source worktree and real profile.

## Full development lane

Freeze and commit intended changes, including the regenerated source manifest.
Use the runner from that candidate and a new output directory with an existing
parent. Example:

~~~powershell
$repo = 'C:\work\CloudScribe'
$sha = (git -C $repo -c core.bare=false rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify candidate.' }
$check = @{
    Repository = $repo
    ExpectedCommit = $sha
    DotnetPath = 'C:\tools\dotnet\dotnet.exe'
    PythonPath = 'C:\tools\Python313\python.exe'
    SchemaPythonPath = 'C:\tools\schema-venv\Scripts\python.exe'
    OutputDirectory = 'C:\evidence\CloudScribe-full-new'
}
& "$repo\scripts\Invoke-DeveloperChecks.ps1" @check
if ($LASTEXITCODE -ne 0) { throw 'Checks failed or incomplete; inspect summary.json.' }
~~~

SDK 10.0.402 is required exactly. DOTNET_ROOT and PATH are scoped to the selected
SDK for child commands and restored when the runner exits. Python 3.13 is
recommended. Omit
-SchemaPythonPath to use the main Python interpreter. Missing jsonschema 4.26.0,
rfc8785 0.1.4 or cryptography 49.0.0 blocks the complete-schema gate; nothing is
installed automatically. Identity-only control validation with a deferral
message is **not** a complete schema pass.

Add -PlanOnly to record tool/source identity and the command inventory without
executing planned checks. It deliberately exits 2, with all planned stages
NOT RUN. It can inspect an uncommitted plan but cannot certify it. Full execution
requires a clean committed checkout.

The full lane runs sequentially:

1. Manifest, locked restore, strict Release build, format and complete
   Microsoft.Testing.Platform tests with --solution. The --no-build test stage
   depends on this invocation's successful strict build, not stale output.
2. Current source verifiers, authenticated controls, separately checked complete
   schema validation, all material regressions and verifier self-tests.
3. Full offline completion-workflow unittest discovery.
4. Current CI dependency-notice and installer/admission/policy PowerShell
   self-tests, including reboot, expiry and unused-diagnostic variants.
5. Focused verification-runner self-tests, then final manifest and Git checks.

An unsuccessful prerequisite blocks dependents. Independent stages continue.
Source-manifest failure blocks source-dependent commands. There are no retries,
forced process termination or arbitrary timeouts. stdout/stderr stream to disk
during a long command. Cancellation leaves the last checkpoint and available
logs; an unfinished stage remains NOT RUN.

summary.json records commit/worktree, runner hash, executable paths/hashes and
versions, commands, per-stage statuses/exits/times, raw skip lines and raw test
summaries. Missing/unparsed test counts are unknown, never guessed. A command
PASS does not erase its skips. artifacts.json records evidence paths, byte
lengths and SHA-256 hashes. Exit 0 means all planned gates passed, 1 means a
failed check/source change, and 2 means blocked/incomplete or plan-only.

For only the focused self-tests (including in CI):

~~~powershell
pwsh -NoProfile -File scripts/Test-VerificationRunners-SelfTests.ps1
if ($LASTEXITCODE -ne 0) { throw 'Runner self-tests failed.' }
~~~

Optional -DotnetPath and -PythonPath select explicit executables. This suite
uses inert synthetic app bytes, mocks process discovery in child processes,
tests real file locks/junction rejection when supported, critical loaded-module gaps
in the app/runtime sibling layout, read-only shortcuts and environment restoration,
and invokes only the
developer runner's plan. It cannot recurse into the full suite. Already present
ffmpeg/ffprobe enable actual decoding of a locally generated half-second tone
and corrupt/playlist/missing-tool cases; otherwise that scope is explicitly SKIPPED.
Synthetic fixtures and evidence remain in a unique temporary directory.

## Read-only native evidence lane

Invoke-NativeAcceptanceEvidence.ps1 is supporting evidence, **not a GUI runner
or acceptance certificate**. It never launches/closes an app, types keys, moves
focus, changes DPI/contrast, reads a real profile/vault, generates provider audio
or deletes fixtures.

Prepare and inspect these inputs first:

- Package root (containing sibling app and runtime directories) and a pinned
  package JSON manifest: schema_version 1,
  candidate_commit (40 hex characters), and nonempty files entries containing
  path, bytes, sha256. Paths are unique relative physical files without
  traversal. The default executable member is app/CloudScribe.exe; use the validated
  -ExecutableRelativePath for another supported package layout. Include all
  runtime/app modules needed for the
  intended Process check. Commit provenance is asserted by the build owner,
  not inferred from binary bytes. For Process, also provide required_loaded_modules:
  an array of pinned relative paths identifying exactly one each of CloudScribe.exe,
  CloudScribe.dll, CloudScribe.Infrastructure.dll, coreclr.dll, hostfxr.dll and
  hostpolicy.dll. Additional explicitly required modules are permitted.
- Separate new CloudScribe-acceptance-* profile and CloudScribe-fixture-*
  directories, containing only nonprivate synthetic fixtures using a supported
  disconnected/fake-provider setup. The script does not invent/apply a profile
  switch or configure a provider.
- Pinned fixture JSON: schema_version 1 and nonempty files entries containing
  path, bytes, sha256 and kind ("source" or "audio").
- Profile marker .cloudscribe-synthetic-fixture.json: schema_version 1,
  synthetic true, disconnected true, contains_private_data false, exact absolute
  profile_path and fixture_path, and fixture_manifest_sha256. This is an
  explicit operator declaration, not proof of app network isolation.
- -ProtectedRoots listing **every inspected** real profile override, real
  audio/library root, backup root and source worktree. Arbitrary custom real
  profiles cannot be discovered reliably by this collector.
- New external output directory. App/profile/fixtures/output must be disjoint.
  Existing outputs, roots, system/user/document-root aliases, unresolved
  parents, overlaps and reparse points are refused before writes.

Audio entries additionally require provenance_id, permitted_scope equal to
"nonprivate-local-acceptance", and positive minimum_duration_seconds and
maximum_duration_seconds. Only a single MP3 audio stream with positive
channels/rate and matching duration is accepted. Supply explicit installed
-FfprobePath and -FfmpegPath to inspect metadata and decode completely to a null
sink. Both commands force the MP3 demuxer and a file-only protocol whitelist;
they never autodetect playlists or follow external references. These assertions do not verify a provider's voice, exact payload,
segment order, complete text, pronunciation or naturalness. No audio is played.

After preparing and checking those inputs:

~~~powershell
$native = @{
    PackageDirectory = 'C:\acceptance\candidate'
    CandidateCommit = $sha
    PackageManifest = 'C:\acceptance\package-manifest.json'
    PackageManifestSha256 = (Get-FileHash 'C:\acceptance\package-manifest.json').Hash
    ProfileDirectory = 'C:\acceptance\CloudScribe-acceptance-review'
    FixtureDirectory = 'C:\acceptance\CloudScribe-fixture-review'
    FixtureManifest = 'C:\acceptance\fixture-manifest.json'
    FixtureManifestSha256 = (Get-FileHash 'C:\acceptance\fixture-manifest.json').Hash
    ProtectedRoots = @('C:\work', 'C:\inspected-real-profile', 'C:\inspected-real-audio', 'C:\backups')
    Phase = 'Before'
    OutputDirectory = 'C:\evidence\native-before-new'
}
& "$repo\scripts\Invoke-NativeAcceptanceEvidence.ps1" @native
# Exit 2 is expected while observation/instrumentation gates remain incomplete.
~~~

**Before/After:** normally close all CloudScribe instances first. Each captured
root is hashed while holding exclusive read handles for its complete file set.
The collector checks process/file-set state, rejects credential-like filenames
and WAL/SHM/journal sidecars, and refuses locks/reparse points/uncertain capture.
Do not concurrently modify fixtures. No database is opened, migrated or queried.

After also requires -BeforeEvidence pointing to generated snapshot.json and
-BeforeEvidenceSha256 holding its independently retained hash. It compares all
paths/lengths/hashes and identity fields. Intended state mutations require a
separate reviewed semantic adapter; changes are never silently allowlisted.
Use a different new output directory for each phase.

**Process:** supply -AppProcessId for an already running test-owned instance.
The exact executable path and every declared critical loaded module are checked.
Missing required modules, including managed assemblies not exposed by the process
inspection API, are BLOCKED rather than inferred from disk. Package-local
modules must match the pinned manifest. A shared runtime outside the package is
hashed but BLOCKED without independent expected identity. No profile snapshot
is attempted in this phase. The process-to-profile binding is not established
by module inspection. Observed disk bytes alone never prove loading.

Optional -ShortcutPath reads an existing .lnk and compares its target. It does
not launch or modify the shortcut or prove that a process came from it.
Isolated target inspection is not installed-shortcut acceptance. Actual launch
and displayed-version observations remain NOT RUN.

## Pending acceptance and evidence

Native summary.json has concrete cases, family/lane/status counts, shared
candidate identity, times, limitations and artifact hashes. Per-case references
link snapshot/audio evidence. The UI/listening/screen-reader/DPI fields always
start NOT RUN. No hash or mocked test fills them in.

Keep actual observations in a separate worksheet with concrete steps,
candidate/process/configuration, expected/actual behavior, timestamps, recording
or screenshot hashes, and settings/restoration. Never overwrite generated
evidence or promote an entire family from a supporting assertion.

Retained cases cover V02 exact source/voice/full-text comparison; V10 beginning,
ending, every join, difficult passages and full coverage; Q12 controls/EOF/reopen;
W07 Voices/populated Batch/delayed-response focus; W08 actual accessibility tree
and screen-reader announcements; and W09 real 100/125/150/200 percent scale,
small/normal windows, long labels and high contrast. Unavailable configurations
remain NOT RUN or BLOCKED, never assumed equivalent.

The isolated catalog-cancellation follow-up makes comparison Close/Escape/native close responsive by retiring UI publication before requesting cancellation. An uncooperative background lookup still owns its loading/diagnostic lease until it unwinds, and a reopened window blocks another catalog request meanwhile. The 30-second timer is not a guaranteed cleanup deadline. Native close/focus acceptance remains pending; headless controls do not certify W07. See [Follow-up fixes](FOLLOW_UP_FIXES.md). Generation/export and diagnostic guards must remain intact.

Logical library/queue/authorization state and app provider/vault counters are
explicitly BLOCKED: no supported adapter/instrumentation is implemented. Those
counts are **null/unknown, not zero**. No provider/vault operations exist in this
runner, but that does not certify a running app's activity.

A useful capture can therefore have script PASS records and still exit 2.
Exit 1 means an assertion failed. Neither authorizes paid requests, real-profile
changes, installation or release. Keep all evidence private and out of clean
distributable packages. Fixtures are retained; no recursive cleanup is performed.
