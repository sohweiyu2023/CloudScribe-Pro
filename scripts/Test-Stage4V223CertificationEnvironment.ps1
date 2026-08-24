[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ExpectedCandidateSha,
  [Parameter(Mandatory=$true)][string]$MasterPackagePath
)
$ErrorActionPreference='Stop'

if (-not $IsWindows) { throw 'Stage4 v2.23 certification requires Windows.' }
if ($ExpectedCandidateSha -notmatch '^[0-9a-f]{40}$') { throw 'Expected candidate SHA must be exact lowercase 40-character hex.' }
if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) { throw 'Authenticated v2.23 master package is missing.' }

$head=(git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCandidateSha) { throw "Candidate drift: expected $ExpectedCandidateSha, got $head" }
$status=@(git status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Certification worktree must be completely clean, including no untracked files.' }

$gitDir=(git rev-parse --git-dir).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $gitDir)) { throw 'Git metadata unavailable.' }
foreach ($tool in @('git','python','dotnet')) {
  if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required certification tool is unavailable: $tool" }
}

$pkg=(Resolve-Path -LiteralPath $MasterPackagePath).Path
if ([IO.Path]::GetExtension($pkg) -ne '.zip') { throw 'Master package must be the authenticated ZIP.' }
$item=Get-Item -LiteralPath $pkg -Force
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Master package path must not be a reparse point.' }
if ($item.Length -le 0) { throw 'Authenticated v2.23 master package is empty.' }

Write-Host "Stage4 environment admitted for exact candidate $head"