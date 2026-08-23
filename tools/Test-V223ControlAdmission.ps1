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

$actual = (Get-FileHash -LiteralPath $MasterPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ([string]$lock.masterPackageSha256).ToLowerInvariant()

if ($actual -ne $expected) {
    throw "v2.23 master package SHA-256 mismatch. Expected $expected, got $actual."
}

Write-Host "v2.23 control admission PASS"
Write-Host "master SHA-256: $actual"
Write-Host "internal manifest admission: $($lock.internalManifestMatched)/$($lock.internalManifestExpected)"
Write-Host "runtime policy: $($lock.runtimePolicyVersion)"
Write-Host "fresh Windows certification required: $($lock.promotionRequiresFreshWindowsCertification)"
