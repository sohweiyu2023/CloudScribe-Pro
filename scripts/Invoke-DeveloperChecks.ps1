#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [string]$Repository = (Split-Path -Parent $PSScriptRoot),
    [string]$DotnetPath = 'dotnet',
    [string]$PythonPath = 'python',
    [string]$SchemaPythonPath = '',
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verification/Verification.Common.ps1')
$repo = Resolve-PhysicalPath $Repository
$out = New-EvidenceDirectory $OutputDirectory @($repo)
$git = $null
$report = [ordered]@{
    schema_version = 1; kind = 'developer-checks'; expected_commit = $ExpectedCommit
    runner_path = $PSCommandPath; runner_sha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    candidate_commit = $null; repository = $repo; started_utc = [DateTime]::UtcNow.ToString('o')
    finished_utc = $null; plan_only = [bool]$PlanOnly; preflight = 'NOT RUN'; reason = ''
    source_status_before = @(); source_status_after = @(); tools = @{}; process_environment = @{}; stages = @(); exit_code = 2; execution_started = $false
    limitations = @('Provider-free development checks, not native/live acceptance or release certification.',
        'Skip lines and raw summaries are retained without inventing test totals.',
        'No dependencies are installed. Missing complete-schema prerequisites block that gate.')
}
$environment = @{}
foreach ($name in @('PATH', 'DOTNET_ROOT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_NOLOGO', 'PYTHONDONTWRITEBYTECODE', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Save-Report { Write-EvidenceJson (Join-Path $out 'summary.json') $report }
function Resolve-Tool([string]$Name) {
    $path = (Get-Command $Name -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    return Resolve-PhysicalPath $path
}
function Read-Identity([string]$Name, [string]$File, [string[]]$Arguments) {
    $base = Join-Path $out ('identity-' + $Name)
    $code = Invoke-LoggedProcess $File $Arguments $repo $base
    if ($code -ne 0) { throw "Tool identity failed: $Name (exit $code)." }
    $version = (Get-Content -LiteralPath ($base + '.stdout.log') -Raw).Trim()
    $report.tools[$Name] = @{ path = $File; sha256 = (Get-FileHash -LiteralPath $File).Hash; version = $version }
    return $version
}
function Add-Stage([string]$Id, [string]$File, [string[]]$Arguments, [string[]]$Dependencies = @(),
    [string]$Directory = $repo, [string]$SuccessText = '', [switch]$Prerequisite) {
    $report.stages += New-CheckStage $Id $File $Arguments $Directory $Dependencies $SuccessText -Prerequisite:$Prerequisite
}
try {
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    # Child verifiers use Git too. Override this process tree, not repository configuration.
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = 'core.bare'
    $env:GIT_CONFIG_VALUE_0 = 'false'
    $git = Resolve-Tool 'git'
    $dotnet = Resolve-Tool $DotnetPath
    $env:DOTNET_ROOT = Split-Path $dotnet
    $python = Resolve-Tool $PythonPath
    $pwsh = Resolve-Tool (Join-Path $PSHOME 'pwsh.exe')
    $schemaPython = if ($SchemaPythonPath) { Resolve-Tool $SchemaPythonPath } else { $python }
    $env:PATH = ((Split-Path $dotnet), (Split-Path $python), $PSHOME, $env:PATH) -join [IO.Path]::PathSeparator
    $report.candidate_commit = (& $git -C $repo -c core.bare=false rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Git candidate.' }
    $report.source_status_before = @(& $git -C $repo -c core.bare=false status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect worktree state.' }
    $report.process_environment = @{ DOTNET_ROOT = $env:DOTNET_ROOT; DOTNET_GENERATE_ASPNET_CERTIFICATE = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE }
    Read-Identity 'git' $git @('--version') | Out-Null
    $sdk = Read-Identity 'dotnet' $dotnet @('--version')
    Read-Identity 'python' $python @('--version') | Out-Null
    Read-Identity 'schema-python' $schemaPython @('--version') | Out-Null
    Read-Identity 'pwsh' $pwsh @('--version') | Out-Null
    if (-not $IsWindows) { throw 'The full documented acceptance lane requires Windows.' }
    if ($sdk -ne '10.0.402') { throw "Exact SDK 10.0.402 required; found $sdk." }
    if ($report.candidate_commit -ne $ExpectedCommit) { throw 'Expected commit does not match this worktree.' }
    if ($env:CLOUDSCRIBE_SYNTHETIC_LONG_AUDIO_MEDIA_TOOLS) { throw 'Unset CLOUDSCRIBE_SYNTHETIC_LONG_AUDIO_MEDIA_TOOLS for the default mocked suite.' }
    Add-Stage 'source-manifest' $python @('-B', 'tools/update_sha256_manifest.py', '--check')
    Add-Stage 'locked-restore' $dotnet @('restore', 'CloudScribe.sln', '--locked-mode', '--disable-parallel', '--configfile', 'NuGet.config') @('source-manifest')
    Add-Stage 'strict-release-build' $dotnet @('build', 'CloudScribe.sln', '-c', 'Release', '--no-restore', '--disable-build-servers', '-m:1', '-nodeReuse:false', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false') @('locked-restore')
    Add-Stage 'format' $dotnet @('format', 'CloudScribe.sln', '--no-restore', '--verify-no-changes') @('locked-restore')
    Add-Stage 'full-mtp-solution' $dotnet @('test', '--solution', 'CloudScribe.sln', '-c', 'Release', '--no-build', '--no-restore', '--output', 'Detailed') @('strict-release-build')
    foreach ($check in @('verify_repository', 'verify_exact_v222_controls', 'verify_project_dependencies', 'verify_stage1_source', 'verify_stage2_source')) {
        Add-Stage $check $python @('-B', "tools/$check.py") @('source-manifest')
    }
    $dependencyProbe = "import importlib.metadata as m; import jsonschema, rfc8785, cryptography; expected={'jsonschema':'4.26.0','rfc8785':'0.1.4','cryptography':'49.0.0'}; actual={k:m.version(k) for k in expected}; print(actual); assert actual == expected, 'Authenticated validator dependency versions are required'"
    Add-Stage 'schema-prerequisites' $schemaPython @('-B', '-c', $dependencyProbe) @('source-manifest') -Prerequisite
    Add-Stage 'complete-control-schema' $schemaPython @('-B', 'tools/verify_exact_v222_controls.py') @('schema-prerequisites') -SuccessText 'runtime-policy 1.3 validation PASS.'
    Add-Stage 'material-regressions' $python @('-B', 'tools/run_python_regression_shards.py', '--all') @('source-manifest')
    Add-Stage 'verifier-self-tests' $python @('-B', 'tools/run_verifier_self_tests.py') @('source-manifest')
    Add-Stage 'completion-python-discovery' $python @('-B', '-m', 'unittest', 'discover', '-s', '.', '-p', 'test_*.py', '-v') @('source-manifest') (Join-Path $repo 'tools/completion_workflow')
    Add-Stage 'third-party-notices' $pwsh @('-NoProfile', '-File', 'scripts/Test-ThirdPartyNotices.ps1', '-AppRoot', './src/CloudScribe.App/bin/Release/net10.0', '-NoticesRoot', './licenses/third-party') @('strict-release-build')
    foreach ($check in @('ThirdPartyNotices', 'InstallCloudScribe', 'RecoveryAdmissionPolicy', 'ChirpArticlePolicy',
        'ArticleVoicePolicy', 'AdditiveArticleAdmission', 'AdditiveArticleBuildRebind', 'ChirpReservationResumePolicy')) {
        Add-Stage "ps-$check" $pwsh @('-NoProfile', '-File', "scripts/Test-$check-SelfTests.ps1") @('strict-release-build')
    }
    foreach ($check in @('CompletionWorkflowGate', 'CompletionInterpreterRepair', 'ExpiredInterpreterSuccessor',
        'SentenceReplan', 'SentenceDiagnostic', 'PendingInstallWithdrawal')) {
        Add-Stage "ps-$check" $pwsh @('-NoProfile', '-File', "scripts/Test-$check-SelfTests.ps1", '-PythonPath', $python) @('strict-release-build')
    }
    foreach ($variant in @(
        @('SentenceReplan', 'AfterReboot'), @('SentenceReplan', 'ExpiredTerminal'),
        @('SentenceDiagnostic', 'ExpiredWindow'), @('SentenceDiagnostic', 'UnusedDiagnostic'))) {
        Add-Stage "ps-$($variant[0])-$($variant[1])" $pwsh @('-NoProfile', '-File',
            "scripts/Test-$($variant[0])-SelfTests.ps1", '-PythonPath', $python, "-$($variant[1])") @('strict-release-build')
    }
    Add-Stage 'verification-runner-self-tests' $pwsh @('-NoProfile', '-File', 'scripts/Test-VerificationRunners-SelfTests.ps1', '-DotnetPath', $dotnet, '-PythonPath', $python) @('source-manifest')
    Add-Stage 'source-manifest-final' $python @('-B', 'tools/update_sha256_manifest.py', '--check')
    $report.preflight = 'PASS'
    if ($PlanOnly) {
        $report.reason = 'Plan only: no planned checks executed.'
    } elseif ($report.source_status_before.Count) {
        throw 'Full verification requires a clean committed worktree. PlanOnly may inspect an uncommitted plan.'
    } else {
        Save-Report
        $report.execution_started = $true
        Invoke-CheckStages $report.stages $out { Save-Report }
        $report.exit_code = Get-EvidenceExitCode $report.stages
    }
} catch {
    $report.preflight = 'BLOCKED'
    $report.reason = $_.Exception.Message
    foreach ($stage in $report.stages) {
        if ($stage.status -eq 'NOT RUN') { $stage.status = 'BLOCKED'; $stage.reason = $report.reason }
    }
    Write-Warning $report.reason
} finally {
    if ($report.candidate_commit -and $git) {
        $report.source_status_after = @(& $git -C $repo -c core.bare=false status --porcelain=v1 --untracked-files=all)
        $statusCode = $LASTEXITCODE
        $headAfter = (& $git -C $repo -c core.bare=false rev-parse HEAD | Out-String).Trim()
        if ($statusCode -ne 0 -or $LASTEXITCODE -ne 0 -or $headAfter -ne $report.candidate_commit -or
            ($report.execution_started -and $report.source_status_after.Count)) {
            $report.reason = 'Source identity changed, worktree became dirty, or final Git inspection failed.'
            $report.exit_code = 1
        }
    }
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    $report.finished_utc = [DateTime]::UtcNow.ToString('o')
    Save-Report
    Write-ArtifactManifest $out
}
Write-Host "Evidence: $out (exit $($report.exit_code))"
exit $report.exit_code
