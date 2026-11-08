# Developer checks and full test suite

Use this guide for a **source checkout**. The public Windows ZIP contains the runnable app and private runtime, not the development solution or tests. Start with [AGENTS.md](../AGENTS.md) for the repository's data-preservation and release rules.

These commands target Windows and the current servicing source. They do not configure a provider, access a real profile, approve spending, install over an existing application, or publish a release. "Offline tests" means provider-free: installing tools and restoring NuGet/Python packages can still require Internet access.

## Reusable checked entry points

For a committed candidate, [Verification runners](VERIFICATION_RUNNERS.md) provides one full-suite command that records exact source/tool identity, per-stage logs, failures, skips and blocked checks. The separate native-evidence collector helps check package, process and synthetic fixture integrity; it never substitutes for actual UI, listening, screen-reader or display acceptance. The detailed manual commands below remain available for focused work and diagnosis.

## 1. Prerequisites and checkout

- Windows. Current CI uses `windows-2025`; do not assume a non-Windows pass covers Windows behavior.
- Git on PATH and a Git checkout for the complete verifier self-tests. A source ZIP can build, but `run_verifier_self_tests.py` uses `git ls-files` to create its temporary historical fixture and therefore needs Git metadata.
- **.NET SDK 10.0.402 exactly**, not just a .NET runtime or a newer SDK. [global.json](../global.json) disables SDK roll-forward.
- PowerShell **7 or later**, available as `pwsh`. Some compiled tests also start `pwsh`; Windows PowerShell 5.1 is not a substitute.
- Python 3. **Python 3.13 is the CI-tested/recommended version**, not a repository-wide exact version pin. The completion tests and ordinary verifier runners use the standard library. The optional complete control-schema check has additional dependencies described below.
- Space for NuGet packages, build outputs and disposable test fixtures. Keep logs, virtual environments, archives and backups outside the source tree.

For a new checkout, run in PowerShell 7 from a parent directory:

```powershell
git clone https://github.com/sohweiyu2023/CloudScribe-Pro.git
if ($LASTEXITCODE -ne 0) { throw 'Clone failed.' }
Set-Location .\CloudScribe-Pro
git rev-parse HEAD
git status --short
```

To reproduce a particular review/release, use its exact commit in a separate clean checkout and record `git rev-parse HEAD`. Do not discard unrelated changes or assume a passing result from another commit covers this checkout.

From the checkout root, verify the tools:

```powershell
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 or later.' }

git --version
if ($LASTEXITCODE -ne 0) { throw 'Git is unavailable.' }

$sdk = dotnet --version
if ($LASTEXITCODE -ne 0 -or "$sdk".Trim() -ne '10.0.402') {
    throw 'Install/select the exact .NET SDK 10.0.402 required by global.json.'
}

$Python = (Get-Command python -ErrorAction Stop).Source
& $Python --version
if ($LASTEXITCODE -ne 0) { throw 'Select a working Python interpreter.' }
```

If `python` does not select your intended interpreter, assign `$Python` its full executable path. For example, if the Windows Python launcher lists an installed 3.13 runtime, `py -3.13 -c "import sys; print(sys.executable)"` prints that path. Use the same interpreter consistently for the commands below. A `-PythonPath` parameter needs the executable path, not the string `py -3.13`.

## 2. Quick development checks

Run from the source root. Stop on the first failure; a later success does not erase an earlier failure.

```powershell
& $Python -B tools/update_sha256_manifest.py --check
if ($LASTEXITCODE -ne 0) { throw 'Source manifest check failed; inspect the changes.' }

dotnet restore CloudScribe.sln --locked-mode --disable-parallel --configfile NuGet.config
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

dotnet format CloudScribe.sln --no-restore --verify-no-changes
if ($LASTEXITCODE -ne 0) { throw 'Formatting check failed.' }

dotnet build CloudScribe.sln -c Release --no-restore --disable-build-servers -m:1 -nodeReuse:false -p:BuildInParallel=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Strict Release build failed.' }
```

For a focused test cycle, run the relevant project or class after rebuilding. This existing class is a small example, not a substitute for testing the area you changed:

```powershell
dotnet test --project tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~Stage8ArchiveExtractionPolicyTests' --output Detailed
if ($LASTEXITCODE -ne 0) { throw 'Focused tests failed.' }
```

For a whole project, omit `--filter`. The four test projects are Domain, Application, Infrastructure and Architecture under `tests/`. Quick/focused success is **not** a full-suite result.

After intentional source/documentation edits, the existing manifest will be stale. Review those changes, freeze the intended source, regenerate the manifest as described in section 7, then rerun the checks. Never regenerate merely to hide an unexplained mismatch.

## 3. Complete compiled solution suite

After the successful restore and Release build above:

```powershell
dotnet test --solution CloudScribe.sln -c Release --no-build --no-restore --output Detailed
if ($LASTEXITCODE -ne 0) { throw 'Complete compiled solution test suite failed.' }
```

Use `--solution` or `--project` explicitly. This repository selects **Microsoft.Testing.Platform** in `global.json`; do not replace the command with old positional-solution/VSTest examples from unrelated projects.

The complete suite includes mocked/local integration, architecture and real installer-function checks against disposable synthetic fixtures. It is not a live Google test. Do not point fixtures at your installed profile or supply credentials. Leave `CLOUDSCRIBE_SYNTHETIC_LONG_AUDIO_MEDIA_TOOLS` unset for the default mocked-media scope; opting into real local media tools is a separate native check.

Expect tens of minutes on some machines, not a few seconds. A recent local compiled run took about 36 minutes; that is context, not a timeout or performance guarantee. Counts and duration depend on the source and host.

Some tests skip when Windows does not permit temporary symbolic-link fixtures, and one export test is explicitly skipped on Windows. Read the actual skip reasons. Five symlink-related skips occurred in a recent Windows run; do not hard-code five as the only acceptable result or treat unrelated skips as covered. Do not elevate privileges or weaken security settings merely to turn a skipped fixture green. Native Windows acceptance remains separate.

Changing code after the build makes `--no-build` results stale. Rebuild and rerun the affected tests; final full-suite evidence must identify the final source commit.

## 4. Source, material-regression and verifier checks

These supplement the compiled suite; they do not replace it. Run from a Git checkout root using the selected Python:

```powershell
$sourceChecks = @(
    'tools/verify_repository.py',
    'tools/verify_exact_v222_controls.py',
    'tools/verify_project_dependencies.py',
    'tools/verify_stage1_source.py',
    'tools/verify_stage2_source.py'
)
foreach ($check in $sourceChecks) {
    & $Python -B $check
    if ($LASTEXITCODE -ne 0) { throw "Source check failed: $check" }
}

& $Python -B tools/run_python_regression_shards.py --all
if ($LASTEXITCODE -ne 0) { throw 'Material regression inventory failed.' }

& $Python -B tools/run_verifier_self_tests.py
if ($LASTEXITCODE -ne 0) { throw 'Verifier self-tests failed.' }
```

The current inventory expects **153 material checks** and **87 verifier self-tests on Windows**, including process-tree defenses. The runner itself checks its expected counts. Non-Windows verifier scope is smaller and is not Windows certification.

The self-test runner projects its Stage4 contract tests into a disposable historical fixture. Do **not** edit the real source to make it look like Stage4, and do not run standalone historical Stage3/Stage4 source verifiers as acceptance for the current Stage8/1.0.x tree.

### Complete control-schema validation

`verify_exact_v222_controls.py` always checks the authenticated eleven-part carrier and locked member identities. If `jsonschema` is absent, it returns success **with an explicit deferral message** after checking identities and the supplied report. That is not execution of the schema/semantic validator.

To perform that additional validation, install the validator's authenticated dependency versions in a dedicated virtual environment **outside** the checkout. These three packages are imported by the supplied validator:

```powershell
$SchemaEnvironment = Join-Path (Split-Path -Parent (Get-Location).Path) 'CloudScribe-schema-venv'
if (Test-Path -LiteralPath $SchemaEnvironment) {
    throw 'Choose a new empty external virtual-environment path or inspect an existing one first.'
}
& $Python -m venv $SchemaEnvironment
if ($LASTEXITCODE -ne 0) { throw 'Virtual environment creation failed.' }

$SchemaPython = Join-Path $SchemaEnvironment 'Scripts\python.exe'
& $SchemaPython -m pip install 'jsonschema[format-nongpl]==4.26.0' 'rfc8785==0.1.4' 'cryptography==49.0.0'
if ($LASTEXITCODE -ne 0) { throw 'Validator dependency installation failed.' }

& $SchemaPython -B tools/verify_exact_v222_controls.py
if ($LASTEXITCODE -ne 0) { throw 'Complete control-schema validation failed.' }
```

Installing only `jsonschema` is insufficient if `rfc8785` or `cryptography` is missing. The embedded bundle also lists `pytest==9.0.2` and `python-docx==1.2.0` for its broader tooling; the repository commands above do not require those two packages. Use the verifier's full success message, not just exit code zero, to distinguish executable validation from deferred validation. The historical report is reproduced at its recorded timestamp; this does not establish current provider pricing or spending authority.

## 5. Full current CI offline/safety coverage

The current [Voice comparison and guarded samples workflow](../.github/workflows/cloudscribe-voice-comparison.yml) runs three independent jobs:

1. Source manifest, locked restore, strict build, format, dependency notices and installer/policy rejection tests.
2. Python completion-workflow discovery tests.
3. The complete compiled solution suite.

Its `mocked-checks` aggregate requires **all three jobs** to succeed. A failed, skipped or cancelled dependency is not a pass. Sections 2 and 3 cover the common build/compiled commands; add both groups below to reproduce the remaining current CI checks locally. Section 4 adds the separate material/governance checks; those are not implied by the current CI aggregate.

### Python completion-workflow suite

```powershell
Push-Location tools/completion_workflow
try {
    & $Python -B -m unittest discover -s . -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Completion-workflow offline tests failed.' }
}
finally {
    Pop-Location
}
```

Discovery is intentional: it includes the newer completion, recovery and stopped-state tests. A hand-picked older module list is only focused coverage. These tests exercise durable journals, subprocesses and synthetic fixtures and can take substantially longer than the quick checks. Let them finish and retain the summary. CI runs the long Python and compiled suites in parallel on separate workers; its default per-job limit is six hours, not a recommended local timeout.

### Windows package and installer/policy self-tests

Run from the repository root **after** the Release build. The following functions merely stop this command sequence when a child check fails; they do not change the scripts or bypass their guards.

```powershell
function Invoke-RepositoryCheck {
    param(
        [Parameter(Mandatory)][string]$Script,
        [string[]]$Arguments = @()
    )
    & pwsh -NoProfile -File $Script @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Repository check failed: $Script" }
}

Invoke-RepositoryCheck './scripts/Test-ThirdPartyNotices.ps1' @(
    '-AppRoot', './src/CloudScribe.App/bin/Release/net10.0',
    '-NoticesRoot', './licenses/third-party'
)

$packageChecks = @(
    'Test-ThirdPartyNotices-SelfTests.ps1',
    'Test-InstallCloudScribe-SelfTests.ps1',
    'Test-RecoveryAdmissionPolicy-SelfTests.ps1',
    'Test-ChirpArticlePolicy-SelfTests.ps1',
    'Test-ArticleVoicePolicy-SelfTests.ps1',
    'Test-AdditiveArticleAdmission-SelfTests.ps1',
    'Test-AdditiveArticleBuildRebind-SelfTests.ps1',
    'Test-ChirpReservationResumePolicy-SelfTests.ps1'
)
foreach ($check in $packageChecks) {
    Invoke-RepositoryCheck "./scripts/$check"
}

$pythonChecks = @(
    'Test-CompletionWorkflowGate-SelfTests.ps1',
    'Test-CompletionInterpreterRepair-SelfTests.ps1',
    'Test-ExpiredInterpreterSuccessor-SelfTests.ps1',
    'Test-SentenceReplan-SelfTests.ps1',
    'Test-SentenceDiagnostic-SelfTests.ps1',
    'Test-PendingInstallWithdrawal-SelfTests.ps1'
)
foreach ($check in $pythonChecks) {
    Invoke-RepositoryCheck "./scripts/$check" @('-PythonPath', $Python)
}

Invoke-RepositoryCheck './scripts/Test-SentenceReplan-SelfTests.ps1' @('-PythonPath', $Python, '-AfterReboot')
Invoke-RepositoryCheck './scripts/Test-SentenceReplan-SelfTests.ps1' @('-PythonPath', $Python, '-ExpiredTerminal')
Invoke-RepositoryCheck './scripts/Test-SentenceDiagnostic-SelfTests.ps1' @('-PythonPath', $Python, '-ExpiredWindow')
Invoke-RepositoryCheck './scripts/Test-SentenceDiagnostic-SelfTests.ps1' @('-PythonPath', $Python, '-UnusedDiagnostic')
```

These self-tests use temporary synthetic fixtures. They are not instructions to run `Install-CloudScribe.ps1` against your real installation. Other specialist scripts can require separately prepared fixture metadata or evidence paths; do not blindly run every `Test-*.ps1` file or substitute a production profile.

If an execution-policy or organization policy blocks a reviewed script, inspect the policy and use the approved signed-script route. Do not weaken managed policy or persistently change security settings to make tests pass.

## 6. Build/publish is not installation or release certification

`BUILD-CLOUDSCRIBE-WINDOWS.cmd` is an app-only build/publish convenience, not a test runner. It checks the exact SDK and replaces the existing sibling `CloudScribe-Windows` output folder. Keep personal files and working installations elsewhere; use a new empty destination with the manual publish command when you need to preserve older output.

For the older frozen source at `4eb33824590ad0bb3eedfa6310bcc3430dba8f2c`, the launcher still checks 10.0.401 instead of the required 10.0.402. Use the manual commands for that older checkout. The developer-tooling follow-up corrects the launcher pin and adds a regression test; it does not rebuild or change the separately packaged application. Do not relax `global.json` to work around the old mismatch.

A runnable build can be published after the Release build using the manual procedure in [BUILDING-WINDOWS.txt](../BUILDING-WINDOWS.txt). Its output must be a new empty physical directory outside the source tree. The publish script calls `python` by name, so a working Python must also be on PATH for that separate step. Do not overwrite your installed app or personal files with development output.

For an actual handoff, follow [clean sharing](SHARING_WINDOWS.md) and [Windows installation/update](WINDOWS_INSTALLATION.md). Inspect the existing installation, close the app normally, preserve the profile and stable shortcut, and complete the required native launch/runtime checks. Never distribute profiles, databases, service-account JSON, credentials, documents, recordings, private admission evidence or rollback folders.

The [Stage8 full-certification workflow](../.github/workflows/cloudscribe-stage8-full-certification.yml) is a separate formal gate with certified lineage, exact-candidate and no-mutation requirements and retained evidence. Historical 1.0.1/Stage3/Stage4 workflow names do not make them current 1.0.6 acceptance commands. Do not rewrite stage governance or loosen a gate merely to get a green result.

Source verification, compiled tests, offline completion tests, real Windows/native behavior, live provider acceptance and formal certification are distinct evidence. None of these developer commands approves paid calls, retries, production profile changes, an upload or release promotion.

## 7. Intentional edits, manifest and final evidence

After reviewing and freezing intended source/documentation changes:

```powershell
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Review whitespace errors before freezing source.' }
git diff --stat

& $Python -B tools/update_sha256_manifest.py
if ($LASTEXITCODE -ne 0) { throw 'Manifest regeneration failed.' }
& $Python -B tools/update_sha256_manifest.py --check
if ($LASTEXITCODE -ne 0) { throw 'Regenerated manifest did not verify.' }

git diff -- SHA256SUMS.txt
git status --short
```

Review the manifest diff before committing. Untracked logs, virtual environments or backups inside the source tree can invalidate the inventory; keep them outside. `bin/`, `obj/` and Python bytecode caches are excluded, but arbitrary evidence directories are not. Preserve all eleven authenticated control carrier parts and the historical verifier-input documents.

After committing the intended source and manifest, rerun required checks on that exact candidate. Record the commit, tool versions, scope, pass/fail/skip totals and log locations. For a clean final verification, confirm `git status --porcelain=v1 --untracked-files=all` is empty and the manifest still verifies. Do not refresh the manifest after unexplained test mutations.

`SESSION_STATE.json` deliberately retains Stage8 **1.0.0** governance while the app is a **1.0.x** servicing release. Do not change that identity to match the executable version.

## Troubleshooting

- **SDK not found/mismatch:** run `dotnet --list-sdks` and `dotnet --version` from the checkout root. Install the exact SDK; do not enable roll-forward or change the pin.
- **Test CLI rejects the solution argument:** use the documented .NET 10 `--solution`/`--project` command and exact SDK.
- **Formatting or analyzer failure:** fix the reported source and rerun the gate. Do not suppress warnings or remove checks to manufacture a pass.
- **Locked restore failure:** resolve the actual feed/network problem or review an intentional dependency change. Do not remove lock files or silently update the dependency graph.
- **Verifier says Git is unavailable/not a repository:** use a proper Git checkout for the complete verifier suite rather than a source ZIP.
- **Control-schema validation deferred:** install the isolated validator dependencies from section 4 and rerun it; do not describe the deferred result as a complete schema pass.
- **A test takes a long time:** retain its output and let the long suite finish. A cancelled/unfinished suite is not a pass.
- **Manifest mismatch:** inspect source changes and unexpected files first; regenerate only for reviewed intentional changes.
- **Windows link-fixture skip:** retain the skip reason and disclose the remaining native coverage. Do not change security policy just to remove the skip.
