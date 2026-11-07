param([string]$Repository,[string]$Profile,[string]$PreviousScope,[string]$NextScope,[string]$Ledger)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1')){
    . (Join-Path $Repository ('tools/long-audio/'+$name))
}
$old=ConvertFrom-LAJson (Read-LABytes $PreviousScope)
$new=ConvertFrom-LAJson (Read-LABytes $NextScope)
$claims=ConvertFrom-LAJson (Read-LABytes $Ledger)
Assert-LAExplicitRegenerationTransition $old $new $claims $Profile
'FreshnessVerified; no installation, network or provider action'
