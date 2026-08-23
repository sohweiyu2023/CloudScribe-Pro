[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MasterPackagePath,
    [Parameter(Mandatory = $true)]
    [string] $CandidateSha,
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedBranch = 'agent/stage4-v223-reconciliation'
$resolvedRoot = (Resolve-Path $RepositoryRoot).Path
Push-Location $resolvedRoot
try {
    $head = (git rev-parse HEAD).Trim()
    $branch = (git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve Git candidate identity.' }
    if ($branch -ne $expectedBranch) { throw "Stage 4 certification must run on $expectedBranch; got '$branch'." }
    if ($CandidateSha -notmatch '^[0-9a-f]{40}$') { throw 'CandidateSha must be a lowercase 40-character Git SHA.' }
    if ($head -ne $CandidateSha) { throw "Candidate SHA mismatch. Expected $CandidateSha, checked out $head." }

    git cat-file -e "$CandidateSha`^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'Candidate SHA does not resolve to a local Git commit object.' }
    $treeBefore = (git rev-parse "$CandidateSha`^{tree}").Trim()
    if ($LASTEXITCODE -ne 0 -or $treeBefore -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve candidate source tree identity.' }

    $trackedChangesBefore = @(git status --porcelain=v1 --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify initial candidate worktree state.' }
    if ($trackedChangesBefore.Count -ne 0) { throw 'Stage 4 certification requires a clean tracked candidate before admission.' }

    if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) { throw 'Authenticated v2.23 master package is unavailable.' }

    & (Join-Path $PSScriptRoot 'Test-V223ControlAdmission.ps1') -MasterPackagePath $MasterPackagePath
    if ($LASTEXITCODE -ne 0) { throw 'v2.23 control admission failed.' }

    $trackedChanges = @(git status --porcelain=v1 --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify candidate worktree state.' }
    if ($trackedChanges.Count -ne 0) { throw 'Tracked candidate bytes changed during Stage 4 certification preparation.' }

    $headAfter = (git rev-parse HEAD).Trim()
    if ($headAfter -ne $CandidateSha) { throw 'Candidate identity changed after v2.23 control admission.' }
    $treeAfter = (git rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0 -or $treeAfter -ne $treeBefore) { throw 'Candidate source tree identity changed during v2.23 admission.' }

    [pscustomobject]@{
        CandidateSha = $CandidateSha
        CandidateTreeSha = $treeBefore
        Branch = $branch
        ControlVersion = 'v2.23'
        AdmissionPassed = $true
        CandidateUnchanged = $true
    } | ConvertTo-Json -Depth 3
}
finally {
    Pop-Location
}
