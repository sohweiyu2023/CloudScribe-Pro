param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCandidateSha
)

$ErrorActionPreference = 'Stop'

$head = (git rev-parse HEAD).Trim()
if ($head -ne $ExpectedCandidateSha.ToLowerInvariant()) {
    throw "Candidate HEAD mismatch: expected $ExpectedCandidateSha, got $head"
}

$path = '.cloudscribe-ci/v223-control-lock.json'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Missing v2.23 control lock: $path"
}

$tracked = (git ls-files --error-unmatch -- $path 2>$null)
if (-not $tracked) {
    throw 'v2.23 control lock must be tracked by Git.'
}

$modeLine = (git ls-tree $ExpectedCandidateSha -- $path).Trim()
if (-not $modeLine) {
    throw 'v2.23 control lock is absent from the exact candidate tree.'
}
if (-not $modeLine.StartsWith('100644 ') -and -not $modeLine.StartsWith('100755 ')) {
    throw "v2.23 control lock must be a regular Git blob; got: $modeLine"
}

$candidateBlob = (git rev-parse "$ExpectedCandidateSha`:$path").Trim()
$worktreeBlob = (git hash-object -- $path).Trim()
if ($candidateBlob -ne $worktreeBlob) {
    throw 'v2.23 control lock worktree bytes differ from the exact candidate tree.'
}

[pscustomobject]@{
    CandidateSha = $head
    ControlLockPath = $path
    ControlLockBlobSha = $candidateBlob
    Tracked = $true
    CandidateTreeExact = $true
}
