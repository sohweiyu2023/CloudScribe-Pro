#Requires -Version 7.2
# Synthetic self-tests for Install-BatchAuthority.ps1. Uses a temporary fake install root only; touches nothing else.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Install-BatchAuthority.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-batch-selftest-' + [Guid]::NewGuid().ToString('N'))
$failures = 0; $passed = 0
function With($Source, [string]$Key, $Value) { $copy = [ordered]@{}; foreach ($k in $Source.Keys) { $copy[$k] = $Source[$k] }; $copy[$Key] = $Value; $copy }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Check([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Host "PASS $Name" } catch { $script:failures++; Write-Host "FAIL $Name : $($_.Exception.Message)" }
}
function Throws([scriptblock]$Body, [string]$Text) {
    try { & $Body } catch { if ($_.Exception.Message -notlike "*$Text*") { throw "Unexpected error: $($_.Exception.Message)" }; return }
    throw "Expected an error containing '$Text'."
}
try {
    $install = Join-Path $root 'install'; [void][IO.Directory]::CreateDirectory($install)
    Set-Content -LiteralPath (Join-Path $install 'cloudscribe-install.json') -Value '{"Product":"CloudScribe Pro"}'
    $studio = Join-Path $root 'Profiles' 'Studio'
    $profileRootSha = & $script -ProfileRootSha256For $studio
    $profile = [ordered]@{
        SchemaVersion = 2; Mode = 'Reviewed'; ProfileRootSha256 = $profileRootSha; AccountStableId = 'a'; ProjectStableId = 'p'; AllowedVoiceStableIds = @()
        AllowedEngines = @('long-audio', 'complete-article'); FailurePolicy = 'SkipAndContinue'; Currency = 'USD'; Scale = 2; PerBatchCeilingMinorUnits = 1924
        RollingCeilingMinorUnits = 7693; RollingWindowDays = 30; MaximumFilesPerBatch = 500; ConsentLifetime = '2.00:00:00'
        ExpiresUtc = '2027-02-01T00:00:00+00:00'; DisplayCurrency = 'SGD'; DisplayRate = 1.30; EstimateMinorUnitsPerMillionCharacters = 3000
    }
    $profilePath = Join-Path $root 'profile.json'; $profile | ConvertTo-Json | Set-Content -LiteralPath $profilePath -NoNewline

    Check 'profile root hash is stable and lower-case' {
        if ($profileRootSha -notmatch '^[0-9a-f]{64}$') { throw 'bad hash' }
        if ((& $script -ProfileRootSha256For ($studio + [IO.Path]::DirectorySeparatorChar)) -cne $profileRootSha) { throw 'trailing separator changed the hash' }
    }
    Check 'wrong hash installs nothing' {
        Throws { & $script -ReviewedProfilePath $profilePath -ReviewedProfileSha256 ('0' * 64) -InstallRoot $install } 'does not match'
        if (Test-Path (Join-Path $install 'batch-authority/profile.json')) { throw 'profile was written' }
    }
    Check 'missing install refuses' { Throws { & $script -ReviewedProfilePath $profilePath -ReviewedProfileSha256 (Sha $profilePath) -InstallRoot (Join-Path $root 'nowhere') } 'not installed' }
    Check 'validate-only writes nothing' {
        [void](& $script -ReviewedProfilePath $profilePath -ReviewedProfileSha256 (Sha $profilePath) -InstallRoot $install -ValidateOnly)
        if (Test-Path (Join-Path $install 'batch-authority')) { throw 'something was written' }
    }
    Check 'request before profile refuses' {
        $request = Join-Path $root 'early.json'
        '{"Format":"cloudscribe-batch-admission-request-v2","BatchId":"' + [Guid]::NewGuid() + '","Items":[],"ProposedConsent":{"Source":"ReviewedInstall","BatchId":"00000000-0000-0000-0000-000000000000","ConsentSha256":"' + ('a' * 64) + '"}}' | Set-Content -LiteralPath $request -NoNewline
        Throws { & $script -ReviewedRequestPath $request -ReviewedRequestSha256 (Sha $request) -InstallRoot $install } 'different batch'
    }
    Check 'unknown engines and old profiles refuse' {
        $bad = Join-Path $root 'bad-engine.json'
        (With $profile 'AllowedEngines' @('fast-and-cheap')) | ConvertTo-Json | Set-Content -LiteralPath $bad -NoNewline
        Throws { & $script -ReviewedProfilePath $bad -ReviewedProfileSha256 (Sha $bad) -InstallRoot $install } 'AllowedEngines'
        (With $profile 'SchemaVersion' 1) | ConvertTo-Json | Set-Content -LiteralPath $bad -NoNewline
        Throws { & $script -ReviewedProfilePath $bad -ReviewedProfileSha256 (Sha $bad) -InstallRoot $install } 'schema'
    }
    Check 'bad mode and ceilings refuse' {
        $bad = Join-Path $root 'bad.json'
        (With $profile 'Mode' 'Unlimited') | ConvertTo-Json | Set-Content -LiteralPath $bad -NoNewline
        Throws { & $script -ReviewedProfilePath $bad -ReviewedProfileSha256 (Sha $bad) -InstallRoot $install } 'Mode must be'
        (With $profile 'RollingCeilingMinorUnits' 10) | ConvertTo-Json | Set-Content -LiteralPath $bad -NoNewline
        Throws { & $script -ReviewedProfilePath $bad -ReviewedProfileSha256 (Sha $bad) -InstallRoot $install } 'Ceilings'
    }
    Check 'profile installs with a pin and a replaced profile is kept' {
        $result = & $script -ReviewedProfilePath $profilePath -ReviewedProfileSha256 (Sha $profilePath) -InstallRoot $install
        $pins = Get-Content -Raw (Join-Path $install 'batch-authority/pins.json') | ConvertFrom-Json
        if ($pins.ProfileSha256 -cne (Sha $profilePath) -or $result.Sha256 -cne $pins.ProfileSha256) { throw 'pin mismatch' }
        [void](& $script -ReviewedProfilePath $profilePath -ReviewedProfileSha256 (Sha $profilePath) -InstallRoot $install)
        if (@(Get-ChildItem (Join-Path $install 'batch-authority') -Filter 'profile.previous-*.json').Count -lt 1) { throw 'previous profile not kept' }
    }
    $batch = [Guid]::NewGuid()
    $consentJson = '{"SchemaVersion":1,"ConsentId":"' + [Guid]::NewGuid() + '","BatchId":"' + $batch + '","Source":"ReviewedInstall","CreatedUtc":"2026-11-08T01:02:03.4567891+08:00","ConsentSha256":"' + ('b' * 64) + '"}'
    $requestPath = Join-Path $root 'request.json'
    ('{"Format":"cloudscribe-batch-admission-request-v2","BatchId":"' + $batch + '","Items":[{},{}],"ProposedConsent":' + $consentJson + '}') | Set-Content -LiteralPath $requestPath -NoNewline
    Check 'consent installs byte-for-byte with a pin' {
        $result = & $script -ReviewedRequestPath $requestPath -ReviewedRequestSha256 (Sha $requestPath) -InstallRoot $install
        $installed = Join-Path $install "batch-authority/consents/$($batch.ToString('N')).json"
        if ((Get-Content -Raw $installed) -cne $consentJson) { throw 'consent bytes changed (dates must not be reformatted)' }
        $pins = Get-Content -Raw (Join-Path $install 'batch-authority/pins.json') | ConvertFrom-Json
        if ($pins.Consents.($batch.ToString('N')) -cne (Sha $installed) -or -not $result.Reminder) { throw 'pin mismatch' }
    }
    Check 'identical re-install is a no-op and a different approval is refused' {
        if (-not (& $script -ReviewedRequestPath $requestPath -ReviewedRequestSha256 (Sha $requestPath) -InstallRoot $install).AlreadyInstalled) { throw 'not a no-op' }
        $other = Join-Path $root 'other.json'
        (Get-Content -Raw $requestPath).Replace(('b' * 64), ('c' * 64)) | Set-Content -LiteralPath $other -NoNewline
        Throws { & $script -ReviewedRequestPath $other -ReviewedRequestSha256 (Sha $other) -InstallRoot $install } 'never replaced'
    }
    Check 'an in-app approval cannot be installed as reviewed' {
        $inApp = Join-Path $root 'inapp.json'
        (Get-Content -Raw $requestPath).Replace('"Source":"ReviewedInstall"', '"Source":"InApp"').Replace($batch.ToString(), ($b2 = [Guid]::NewGuid()).ToString()) | Set-Content -LiteralPath $inApp -NoNewline
        Throws { & $script -ReviewedRequestPath $inApp -ReviewedRequestSha256 (Sha $inApp) -InstallRoot $install } 'not a reviewed approval'
    }
}
finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
Write-Host "$passed passed, $failures failed"
if ($failures -gt 0) { exit 1 }
