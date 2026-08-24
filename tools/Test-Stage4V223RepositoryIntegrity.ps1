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

$objectType = (git cat-file -t $ExpectedCandidateSha).Trim()
if ($LASTEXITCODE -ne 0 -or $objectType -ne 'commit') {
    throw 'Expected candidate does not resolve to a Git commit object.'
}

$tree = (git rev-parse "$ExpectedCandidateSha^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') {
    throw 'Expected candidate tree object could not be resolved.'
}

& git fsck --strict --no-reflogs --no-progress
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

if ((git rev-parse HEAD).Trim() -ne $ExpectedCandidateSha) {
    throw 'Repository identity changed during integrity verification.'
}
if ((git rev-parse 'HEAD^{tree}').Trim() -ne $tree) {
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
    IntegrityPassed = $true
} | ConvertTo-Json -Compress
