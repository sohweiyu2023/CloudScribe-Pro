[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MasterPackagePath
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Stage 4 v2.23 certification requires a Windows runner.'
}
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Stage 4 v2.23 certification requires an x64 Windows runner.'
}
if (-not [System.IO.Path]::IsPathFullyQualified($MasterPackagePath)) {
    throw 'The v2.23 master package path must be absolute.'
}
if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) {
    throw 'The authenticated v2.23 master package is not present on the runner.'
}

$package = Get-Item -LiteralPath $MasterPackagePath -Force
if (($package.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The authenticated v2.23 master package must not be supplied through a reparse-point indirection.'
}
if ($package.Length -le 0) {
    throw 'The authenticated v2.23 master package is empty.'
}

$controlLockPath = Join-Path $PSScriptRoot '..\.cloudscribe-ci\v223-control-lock.json'
if (-not (Test-Path -LiteralPath $controlLockPath -PathType Leaf)) {
    throw 'The repository v2.23 control lock is missing.'
}
$controlLock = Get-Content -LiteralPath $controlLockPath -Raw | ConvertFrom-Json
$expectedPackageSha256 = [string]$controlLock.masterPackageSha256
if ($expectedPackageSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'The repository v2.23 control lock does not contain a valid master package SHA-256.'
}
$observedPackageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($observedPackageSha256 -ne $expectedPackageSha256.ToLowerInvariant()) {
    throw "The supplied v2.23 master package SHA-256 does not match the exact repository control lock. Observed: $observedPackageSha256"
}

$git = Get-Command git -ErrorAction Stop
$python = Get-Command python -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace($git.Source) -or [string]::IsNullOrWhiteSpace($python.Source)) {
    throw 'Required certification tools are not resolvable on PATH.'
}

$pythonVersion = & python -c "import sys; print('.'.join(map(str, sys.version_info[:3])))"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pythonVersion)) {
    throw 'Python preflight failed.'
}

[pscustomobject]@{
    Windows = $true
    Architecture = 'X64'
    PackagePath = $package.FullName
    PackageLength = $package.Length
    PackageSha256 = $observedPackageSha256
    PythonVersion = $pythonVersion.Trim()
} | ConvertTo-Json -Compress
