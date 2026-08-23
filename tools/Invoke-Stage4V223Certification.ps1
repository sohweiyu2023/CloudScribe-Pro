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
    if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) { throw 'Authenticated v2.23 master package is unavailable.' }

    & (Join-Path $PSScriptRoot 'Test-V223ControlAdmission.ps1') -MasterPackagePath $MasterPackagePath
    if ($LASTEXITCODE -ne 0) { throw 'v2.23 control admission failed.' }

    $trackedChanges = @(git status --porcelain=v1 --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify candidate worktree state.' }
    if ($trackedChanges.Count -ne 0) { throw 'Tracked candidate bytes changed during Stage 4 certification preparation.' }

    $headAfter = (git rev-parse HEAD).Trim()
    if ($headAfter -ne $CandidateSha) { throw 'Candidate identity changed after v2.23 control admission.' }

    [pscustomobject]@{
        CandidateSha = $CandidateSha
        Branch = $branch
        ControlVersion = 'v2.23'
        AdmissionPassed = $true
        CandidateUnchanged = $true
    } | ConvertTo-Json -Depth 3
}
finally {
    Pop-Location
}
