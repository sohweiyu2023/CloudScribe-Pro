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

if (@(git status --porcelain=v1 --untracked-files=no).Count -ne 0) {
    throw 'Repository integrity gate requires a clean tracked worktree.'
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

if ((git rev-parse HEAD).Trim() -ne $ExpectedCandidateSha) {
    throw 'Repository identity changed during integrity verification.'
}
if ((git rev-parse 'HEAD^{tree}').Trim() -ne $tree) {
    throw 'Repository tree identity changed during integrity verification.'
}
if (@(git status --porcelain=v1 --untracked-files=no).Count -ne 0) {
    throw 'Repository integrity verification changed tracked candidate bytes.'
}

[pscustomobject]@{
    CandidateSha = $ExpectedCandidateSha
    TreeSha = $tree
    IntegrityPassed = $true
} | ConvertTo-Json -Compress
