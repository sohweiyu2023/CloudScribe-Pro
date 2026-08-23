param(
    [Parameter(Mandatory = $true)]
    [string]$MasterPackagePath,

    [string]$ControlLockPath = ".cloudscribe-ci/v223-control-lock.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $MasterPackagePath -PathType Leaf)) {
    throw "v2.23 master package not found: $MasterPackagePath"
}

if (-not (Test-Path -LiteralPath $ControlLockPath -PathType Leaf)) {
    throw "v2.23 control lock not found: $ControlLockPath"
}

$lock = Get-Content -LiteralPath $ControlLockPath -Raw | ConvertFrom-Json

if ($lock.controlVersion -ne "2.23") {
    throw "Unexpected control version: $($lock.controlVersion)"
}

if ($lock.runtimePolicyVersion -ne "1.4") {
    throw "Unexpected runtime policy version: $($lock.runtimePolicyVersion)"
}

if ($lock.internalManifestExpected -ne 628 -or $lock.internalManifestMatched -ne 628) {
    throw "v2.23 internal manifest admission must remain 628/628."
}

if ($lock.stage4PreservedBase -ne "5935ef2110b76235ef626ac8b3340952d6ec4210") {
    throw "Unexpected preserved Stage 4 base."
}

if ($lock.promotionRequiresFreshWindowsCertification -ne $true) {
    throw "Fresh Windows certification must remain mandatory."
}

$requiredIds = @("CACHE-001", "CACHE-002", "PRICE-MAINT-001", "TOOL-001")
foreach ($requiredId in $requiredIds) {
    if ($lock.requiredRequirementIds -notcontains $requiredId) {
        throw "Required v2.23 requirement id missing from control lock: $requiredId"
    }
}

$actual = (Get-FileHash -LiteralPath $MasterPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ([string]$lock.masterPackageSha256).ToLowerInvariant()
if ($actual -ne $expected) {
    throw "v2.23 master package SHA-256 mismatch. Expected $expected, got $actual."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipMemberSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)]
        [string]$Member
    )

    $entry = $Archive.GetEntry($Member)
    if ($null -eq $entry) {
        throw "Required v2.23 control member missing from master package: $Member"
    }

    $stream = $entry.Open()
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $digest = $sha.ComputeHash($stream)
            return ([Convert]::ToHexString($digest)).ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $MasterPackagePath))
try {
    foreach ($property in $lock.controls.PSObject.Properties) {
        $control = $property.Value
        $member = [string]$control.member
        $expectedMemberHash = ([string]$control.sha256).ToLowerInvariant()
        $actualMemberHash = Get-ZipMemberSha256 -Archive $archive -Member $member
        if ($actualMemberHash -ne $expectedMemberHash) {
            throw "v2.23 control member SHA-256 mismatch for $($property.Name). Expected $expectedMemberHash, got $actualMemberHash."
        }
        Write-Host "control PASS: $($property.Name) $actualMemberHash"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "v2.23 control admission PASS"
Write-Host "master SHA-256: $actual"
Write-Host "internal manifest admission: $($lock.internalManifestMatched)/$($lock.internalManifestExpected)"
Write-Host "runtime policy: $($lock.runtimePolicyVersion)"
Write-Host "required ids: $($requiredIds -join ', ')"
Write-Host "fresh Windows certification required: $($lock.promotionRequiresFreshWindowsCertification)"
