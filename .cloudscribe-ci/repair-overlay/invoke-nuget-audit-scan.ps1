[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Project
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'CloudScribe NuGet audit requires PowerShell 7 or later.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = if ([IO.Path]::IsPathRooted($Project)) {
    [IO.Path]::GetFullPath($Project)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $Project))
}
$rootPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $projectPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($projectPath) -ne '.csproj' -or
    -not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Audit target must be an existing project beneath the CloudScribe repository: $Project"
}
$projectItem = Get-Item -LiteralPath $projectPath -Force
if (($projectItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Audit target must not be a symbolic link or reparse point: $Project"
}
$cursor = $projectItem.Directory
while ($null -ne $cursor -and -not [String]::Equals($cursor.FullName, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Audit target must not traverse a symbolic link or reparse point: $Project"
    }
    $cursor = $cursor.Parent
}
if ($null -eq $cursor) {
    throw "Audit target must resolve beneath the CloudScribe repository: $Project"
}
$projectPath = $projectItem.FullName

Set-Location $repoRoot
$maximumAttempts = 3
for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
    $restoreOutput = @(& dotnet restore $projectPath --locked-mode --disable-parallel --configfile NuGet.config `
        --force --no-http-cache -p:CloudScribeNuGetAuditPipeline=true 2>&1)
    $restoreStatus = $LASTEXITCODE
    foreach ($line in $restoreOutput) {
        [Console]::Error.WriteLine($line.ToString())
    }

    if ($restoreStatus -eq 0) {
        break
    }
    $restoreText = $restoreOutput -join [Environment]::NewLine
    if ($attempt -eq $maximumAttempts -or $restoreText -notmatch '(?<![A-Za-z0-9])(NU1900|NU1301)(?![A-Za-z0-9])') {
        exit $restoreStatus
    }

    $delaySeconds = if ($attempt -eq 1) { 2 } else { 5 }
    [Console]::Error.WriteLine(
        "Strict NuGet audit restore hit transient source failure NU1900/NU1301; retrying attempt $($attempt + 1)/$maximumAttempts in $delaySeconds seconds.")
    Start-Sleep -Seconds $delaySeconds
}

& dotnet package list --project $projectPath --vulnerable --include-transitive --no-restore --format json --output-version 1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
