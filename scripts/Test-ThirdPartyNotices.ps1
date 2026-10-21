[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AppRoot,
    [Parameter(Mandatory=$true)][string]$NoticesRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$app = (Resolve-Path -LiteralPath $AppRoot).Path
$notices = (Resolve-Path -LiteralPath $NoticesRoot).Path
$inventory = Get-Content -LiteralPath (Join-Path $notices 'components.json') -Raw | ConvertFrom-Json
if ($inventory.schema_version -ne 1) { throw 'Unsupported third-party notice inventory version.' }
$deps = Get-Content -LiteralPath (Join-Path $app 'CloudScribe.deps.json') -Raw | ConvertFrom-Json
$accounted = @{}
$checkedNotices = @{}
function Get-SafeChild([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'Unsafe relative path in third-party inventory.'
    }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Third-party inventory path escapes its root.'
    }
    return $full
}
function Test-ExactFile([string]$Path, [string]$ExpectedHash, [string]$Label) {
    if ($ExpectedHash -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid SHA-256 for $Label" }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $Label" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $ExpectedHash) { throw "Changed $Label" }
}
foreach ($component in $inventory.components) {
    $identity = $component.package + '/' + $component.version
    if (-not $deps.libraries.PSObject.Properties[$identity]) { throw "Dependency version is absent from app metadata: $identity" }
    if (@($component.notice_files).Count -eq 0 -or @($component.distributed_files).Count -eq 0) {
        throw "Incomplete third-party inventory component: $identity"
    }
    foreach ($notice in $component.notice_files) {
        $path = Get-SafeChild $notices $notice.path
        Test-ExactFile $path $notice.sha256 "notice $($notice.path)"
        $checkedNotices[$notice.path] = $true
    }
    foreach ($asset in $component.distributed_files) {
        $path = Get-SafeChild $app $asset.path
        Test-ExactFile $path $asset.sha256 "dependency asset $($asset.path)"
        if ($accounted.ContainsKey($asset.path) -and $accounted[$asset.path] -ne $identity) {
            throw "Ambiguous ownership for dependency asset $($asset.path)"
        }
        $accounted[$asset.path] = $identity
    }
}
$firstParty = @('CloudScribe.dll','CloudScribe.Application.dll','CloudScribe.Domain.dll','CloudScribe.Infrastructure.dll','CloudScribe.Providers.Abstractions.dll')
if (@(Compare-Object $firstParty @($inventory.first_party_dlls)).Count -ne 0) { throw 'Unexpected first-party DLL exclusion list.' }
$distributed = @(Get-ChildItem -LiteralPath $app -File -Filter '*.dll')
foreach ($platform in @('win','win-x64')) {
    $native = Join-Path $app "runtimes/$platform"
    if (Test-Path -LiteralPath $native -PathType Container) {
        $distributed += @(Get-ChildItem -LiteralPath $native -Recurse -File -Filter '*.dll')
    }
}
foreach ($file in $distributed) {
    $relative = $file.FullName.Substring($app.Length + 1).Replace('\','/')
    if ($relative -notin $firstParty -and -not $accounted.ContainsKey($relative)) {
        throw "Unaccounted redistributed dependency DLL: $relative"
    }
}
Write-Output "Third-party notices PASS: $(@($inventory.components).Count) package records, $($accounted.Count) dependency files, $($checkedNotices.Count) exact notice/license texts."
