[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ExpectedCandidateSha,
  [Parameter(Mandatory=$true)][string]$MasterPackagePath
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'Stage4 v2.23 certification requires Windows.' }
if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) { throw 'Authenticated v2.23 master package is missing.' }
$head=(git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCandidateSha) { throw "Candidate drift: expected $ExpectedCandidateSha, got $head" }
$dirty=git status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Tracked worktree is not clean.' }
$gitDir=(git rev-parse --git-dir).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $gitDir)) { throw 'Git metadata unavailable.' }
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
if (-not $dotnet) { throw 'dotnet unavailable.' }
$pkg=(Resolve-Path -LiteralPath $MasterPackagePath).Path
if ([IO.Path]::GetExtension($pkg) -ne '.zip') { throw 'Master package must be the authenticated ZIP.' }
Write-Host "Stage4 environment admitted for exact candidate $head"