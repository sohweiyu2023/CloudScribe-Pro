#Requires -Version 7.2
<#
.SYNOPSIS
Installs CloudScribe batch narration authority: the one-time batch profile, or one reviewed batch approval.

.DESCRIPTION
The app never writes to the install folder; only this reviewed script does. Each installed file's SHA-256 is pinned in
batch-authority\pins.json, so the app ignores any file that is edited or partly written. Installing a batch approval admits its Long Audio files
directly (within the installed Long Audio scope's voice, bucket and total cap). Its ordinary-engine files, if any, still need
the matching complete-article policy (schema 3, WholeArticleGeneration naming their plan hashes), installed separately with
Install-CloudScribe.ps1 -ReviewedChirpArticlePolicyPath.

Nothing is deleted. A replaced profile is kept as profile.previous-<UTC time>.json. An installed batch approval is never
replaced; re-installing the identical approval is a no-op.

.EXAMPLE
pwsh -File Install-BatchAuthority.ps1 -ProfileRootSha256For "$env:LOCALAPPDATA\CloudScribe Pro\Profiles\Studio"
.EXAMPLE
pwsh -File Install-BatchAuthority.ps1 -ReviewedProfilePath .\batch-profile.json -ReviewedProfileSha256 <sha256>
.EXAMPLE
pwsh -File Install-BatchAuthority.ps1 -ReviewedRequestPath .\batch-admission-request-<id>.json -ReviewedRequestSha256 <sha256>
#>
[CmdletBinding(DefaultParameterSetName = 'Profile')]
param(
    [Parameter(ParameterSetName = 'Profile', Mandatory)][string]$ReviewedProfilePath,
    [Parameter(ParameterSetName = 'Profile', Mandatory)][string]$ReviewedProfileSha256,
    [Parameter(ParameterSetName = 'Request', Mandatory)][string]$ReviewedRequestPath,
    [Parameter(ParameterSetName = 'Request', Mandatory)][string]$ReviewedRequestSha256,
    [Parameter(ParameterSetName = 'Hash', Mandatory)][string]$ProfileRootSha256For,
    [Parameter(ParameterSetName = 'Profile')][Parameter(ParameterSetName = 'Request')]
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CloudScribe Pro'),
    [Parameter(ParameterSetName = 'Profile')][Parameter(ParameterSetName = 'Request')][switch]$ValidateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }

function Read-Reviewed([string]$Path, [string]$Sha, [int]$Maximum) {
    if ($Sha -notmatch '^[0-9a-fA-F]{64}$') { throw 'The reviewed SHA-256 must be 64 hexadecimal characters.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($full)) { throw 'The reviewed file does not exist.' }
    $bytes = [IO.File]::ReadAllBytes($full)
    if ($bytes.Length -gt $Maximum) { throw 'The reviewed file is too large.' }
    if ((Get-Sha256 $bytes) -cne $Sha.ToLowerInvariant()) { throw 'The reviewed file does not match its SHA-256. Nothing was installed.' }
    try { $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes), [Text.Json.JsonDocumentOptions]::new()) } catch { throw 'The reviewed file is not valid JSON. Nothing was installed.' }
    [pscustomobject]@{ Bytes = $bytes; Root = $document.RootElement }
}

function Get-Prop($Element, [string]$Name, [Text.Json.JsonValueKind]$Kind) {
    $value = [Text.Json.JsonElement]::new()
    if (-not $Element.TryGetProperty($Name, [ref]$value) -or $value.ValueKind -ne $Kind) { throw "The reviewed file has no valid '$Name'." }
    $value
}

function Write-Atomic([string]$Path, [byte[]]$Bytes) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllBytes($temporary, $Bytes)
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Read-Pins([string]$Directory) {
    $path = Join-Path $Directory 'pins.json'
    if (-not [IO.File]::Exists($path)) { return [ordered]@{ SchemaVersion = 1; ProfileSha256 = ''; Consents = [ordered]@{} } }
    $root = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new([IO.File]::ReadAllBytes($path)), [Text.Json.JsonDocumentOptions]::new()).RootElement
    $pins = [ordered]@{ SchemaVersion = 1; ProfileSha256 = (Get-Prop $root 'ProfileSha256' String).GetString(); Consents = [ordered]@{} }
    $consents = [Text.Json.JsonElement]::new()
    if ($root.TryGetProperty('Consents', [ref]$consents) -and $consents.ValueKind -eq 'Object') {
        foreach ($entry in $consents.EnumerateObject()) { $pins.Consents[$entry.Name] = $entry.Value.GetString() }
    }
    $pins
}

function Write-Pins([string]$Directory, $Pins) {
    Write-Atomic (Join-Path $Directory 'pins.json') ([Text.Encoding]::UTF8.GetBytes(($Pins | ConvertTo-Json -Depth 4)))
}

if ($PSCmdlet.ParameterSetName -eq 'Hash') {
    # Same as InstalledRecoveryAdmissionStore.ProfileHash: SHA-256 of the full path, trailing separators removed, upper-cased.
    $normal = [IO.Path]::GetFullPath($ProfileRootSha256For).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
    return Get-Sha256 ([Text.Encoding]::UTF8.GetBytes($normal))
}

$install = [IO.Path]::GetFullPath($InstallRoot)
if (-not [IO.File]::Exists((Join-Path $install 'cloudscribe-install.json'))) { throw 'CloudScribe Pro is not installed at the install root. Nothing was installed.' }
$directory = Join-Path $install 'batch-authority'

if ($PSCmdlet.ParameterSetName -eq 'Profile') {
    $file = Read-Reviewed $ReviewedProfilePath $ReviewedProfileSha256 65536
    $root = $file.Root
    if ((Get-Prop $root 'SchemaVersion' Number).GetInt32() -ne 2) { throw 'Unsupported batch profile schema (expected 2).' }
    if ((Get-Prop $root 'Mode' String).GetString() -cnotin @('Off', 'Reviewed', 'InAppWithinLimits')) { throw 'Mode must be Off, Reviewed or InAppWithinLimits.' }
    if ((Get-Prop $root 'ProfileRootSha256' String).GetString() -notmatch '^[0-9a-f]{64}$') { throw 'ProfileRootSha256 must be a lower-case SHA-256 (use -ProfileRootSha256For).' }
    $engines = @((Get-Prop $root 'AllowedEngines' Array).EnumerateArray() | ForEach-Object { $_.GetString() })
    if ($engines.Count -eq 0 -or @($engines | Where-Object { $_ -cnotin @('complete-article', 'long-audio') }).Count -gt 0) { throw 'AllowedEngines must list complete-article and/or long-audio.' }
    $perBatch = (Get-Prop $root 'PerBatchCeilingMinorUnits' Number).GetInt64()
    $rolling = (Get-Prop $root 'RollingCeilingMinorUnits' Number).GetInt64()
    if ($perBatch -lt 1 -or $rolling -lt $perBatch) { throw 'Ceilings must be positive and the rolling ceiling at least the per-batch ceiling.' }
    foreach ($name in 'AccountStableId', 'ProjectStableId', 'Currency', 'DisplayCurrency', 'ExpiresUtc', 'ConsentLifetime') { [void](Get-Prop $root $name String) }
    if ($ValidateOnly) { return [pscustomobject]@{ Valid = $true; Kind = 'Profile'; Directory = $directory } }
    [void][IO.Directory]::CreateDirectory((Join-Path $directory 'consents'))
    $target = Join-Path $directory 'profile.json'
    if ([IO.File]::Exists($target)) {
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture)
        Copy-Item -LiteralPath $target -Destination (Join-Path $directory "profile.previous-$stamp.json")
    }
    $pins = Read-Pins $directory
    Write-Atomic $target $file.Bytes
    $pins.ProfileSha256 = Get-Sha256 $file.Bytes
    Write-Pins $directory $pins
    return [pscustomobject]@{ Installed = 'Profile'; Sha256 = $pins.ProfileSha256 }
}

$request = Read-Reviewed $ReviewedRequestPath $ReviewedRequestSha256 (8 * 1024 * 1024)
if ((Get-Prop $request.Root 'Format' String).GetString() -cne 'cloudscribe-batch-admission-request-v2') { throw 'This is not a CloudScribe batch request (expected format v2).' }
$batchId = [Guid]::Parse((Get-Prop $request.Root 'BatchId' String).GetString())
$consent = Get-Prop $request.Root 'ProposedConsent' Object
if ((Get-Prop $consent 'Source' String).GetString() -cne 'ReviewedInstall') { throw 'The proposed approval is not a reviewed approval.' }
if ([Guid]::Parse((Get-Prop $consent 'BatchId' String).GetString()) -ne $batchId) { throw 'The proposed approval belongs to a different batch.' }
if ((Get-Prop $consent 'ConsentSha256' String).GetString() -notmatch '^[0-9a-f]{64}$') { throw 'The proposed approval has no hash.' }
$pins = Read-Pins $directory
if (-not $pins.ProfileSha256) { throw 'Install the batch profile first.' }
# The raw JSON is copied byte-for-byte, so no date or number is reformatted before the app checks its hash.
$bytes = [Text.Encoding]::UTF8.GetBytes($consent.GetRawText())
$key = $batchId.ToString('N')
$target = Join-Path (Join-Path $directory 'consents') "$key.json"
if ($pins.Consents.Contains($key)) {
    if ($pins.Consents[$key] -ceq (Get-Sha256 $bytes)) { return [pscustomobject]@{ Installed = 'Consent'; BatchId = $key; AlreadyInstalled = $true } }
    throw 'A different approval for this batch is already installed. It is never replaced; create a new batch instead.'
}
if ($ValidateOnly) { return [pscustomobject]@{ Valid = $true; Kind = 'Consent'; BatchId = $key; Files = $request.Root.GetProperty('Items').GetArrayLength() } }
[void][IO.Directory]::CreateDirectory((Join-Path $directory 'consents'))
Write-Atomic $target $bytes
$pins.Consents[$key] = Get-Sha256 $bytes
Write-Pins $directory $pins
[pscustomobject]@{ Installed = 'Consent'; BatchId = $key; Sha256 = $pins.Consents[$key]; Reminder = 'Long Audio files are admitted now. Ordinary-engine files in this request also need their complete-article policy.' }
