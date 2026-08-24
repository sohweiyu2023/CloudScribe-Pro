[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCandidateSha
)

$ErrorActionPreference = 'Stop'

$head = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCandidateSha) {
    throw 'Repository integrity gate is not running at the exact expected candidate SHA.'
}

if (@(git status --porcelain=v1).Count -ne 0) {
    throw 'Repository integrity gate requires a completely clean worktree, including no untracked files.'
}

# Git replace refs can make an apparently exact commit/tree resolve to substituted objects.
# Certification must operate on the repository's real object graph only.
$replaceRefs = @(git for-each-ref --format='%(refname)' refs/replace)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect Git replace refs.'
}
if ($replaceRefs.Count -ne 0) {
    throw 'Repository integrity gate forbids Git replace refs during certification.'
}

$gitDir = (git rev-parse --git-dir).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitDir)) {
    throw 'Unable to resolve Git metadata directory.'
}
if (-not [System.IO.Path]::IsPathRooted($gitDir)) {
    $gitDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $gitDir))
}
$graftsPath = Join-Path $gitDir 'info/grafts'
if (Test-Path -LiteralPath $graftsPath -PathType Leaf) {
    throw 'Repository integrity gate forbids legacy Git grafts during certification.'
}

$isShallow = (git rev-parse --is-shallow-repository).Trim()
if ($LASTEXITCODE -ne 0 -or $isShallow -notin @('true', 'false')) {
    throw 'Unable to determine whether the repository is shallow.'
}
if ($isShallow -ne 'false') {
    throw 'Repository integrity gate requires a complete non-shallow repository.'
}

$objectType = (git --no-replace-objects cat-file -t $ExpectedCandidateSha).Trim()
if ($LASTEXITCODE -ne 0 -or $objectType -ne 'commit') {
    throw 'Expected candidate does not resolve to a Git commit object.'
}

$tree = (git --no-replace-objects rev-parse "$ExpectedCandidateSha^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') {
    throw 'Expected candidate tree object could not be resolved.'
}

& git --no-replace-objects fsck --strict --no-reflogs --no-progress
if ($LASTEXITCODE -ne 0) {
    throw 'Git object integrity verification failed.'
}

$controlLockResult = ./tools/Test-Stage4V223ControlLockTracking.ps1 -ExpectedCandidateSha $ExpectedCandidateSha
if ($LASTEXITCODE -ne 0) {
    throw 'v2.23 control-lock candidate provenance verification failed.'
}
if (-not $controlLockResult) {
    throw 'v2.23 control-lock provenance gate returned no evidence.'
}

if ((git --no-replace-objects rev-parse HEAD).Trim() -ne $ExpectedCandidateSha) {
    throw 'Repository identity changed during integrity verification.'
}
if ((git --no-replace-objects rev-parse 'HEAD^{tree}').Trim() -ne $tree) {
    throw 'Repository tree identity changed during integrity verification.'
}
if (@(git status --porcelain=v1).Count -ne 0) {
    throw 'Repository integrity verification changed or introduced candidate files.'
}

[pscustomobject]@{
    CandidateSha = $ExpectedCandidateSha
    TreeSha = $tree
    ControlLockProvenancePassed = $true
    WorktreeCompletelyClean = $true
    ReplaceRefsAbsent = $true
    LegacyGraftsAbsent = $true
    RepositoryComplete = $true
    IntegrityPassed = $true
} | ConvertTo-Json -Compress
