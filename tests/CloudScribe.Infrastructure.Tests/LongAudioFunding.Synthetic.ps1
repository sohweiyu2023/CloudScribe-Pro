param($Repository,$Install,$Profile)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'tools/long-audio/Provisioning.Files.ps1')
$writer=Join-Path $Repository 'scripts/Update-LongAudioExcerpt.ps1'
function Pin([string]$p){Get-LAHash (Read-LABytes $p)}
$data=Join-Path $Profile 'data/google-long-audio';$ledgerPath=Join-Path $data 'budget-ledger.json';$scopePath=Join-Path $Install 'google-long-audio-scope.json'
$initial=ConvertFrom-LAJson (Read-LABytes $ledgerPath);$claimsSha=Get-LAHash (ConvertTo-LABytes @($initial.Claims))
$checks=0
foreach($stage in @('PredecessorArchived','Prepared','ScopeWritten','LedgerWritten','MarkerWritten','Complete')){
 $fixtureInput=Join-Path ([IO.Path]::GetDirectoryName($Profile)) ('funding-synthetic-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($fixtureInput)
 $old=ConvertFrom-LAJson (Read-LABytes $scopePath);$next=ConvertFrom-LAJson (Read-LABytes $scopePath)
 $next.TotalGoogleCapUsdCents=[long]$old.TotalGoogleCapUsdCents+100
 if($stage -ceq 'Complete'){$next.TaxFxProvisionUsdCents=[long]$old.TaxFxProvisionUsdCents+100}
 $next.SourceApprovalSha256=Get-LAHash ([Text.Encoding]::UTF8.GetBytes('INERT SYNTHETIC FUNDING '+$stage))
 $newPath=Join-Path $fixtureInput 'scope.json';Write-LAAtomic $newPath (ConvertTo-LABytes $next)
 $ledger=ConvertFrom-LAJson (Read-LABytes $ledgerPath);$pins=@()
 foreach($claim in $ledger.Claims){
  $stem=([Guid]$claim.AttemptId).ToString('N');$p=Join-Path $data ('operations/'+$stem+'.json');$op=ConvertFrom-LAJson (Read-LABytes $p)
  $pin=[ordered]@{AttemptId=$claim.AttemptId;Sha256=(Pin $p)}
  if($op.State -eq 2){$s=Join-Path $data ('output/'+$stem);$media=if(Test-Path ($s+'.recovery-verified.json')){$s+'.recovery-verified.json'}else{$s+'.verified.json'};$pin.CompletedReceiptSha256=Pin $media;$pin.CompletedObjectSha256=Pin ($s+'.object.json')}
  $pins+=,$pin
 }
 $target=$pins[0]
 $plan=[ordered]@{Version=6;SourceApprovalSha256=$next.SourceApprovalSha256;PreviousScopeSha256=(Pin $scopePath);PreviousLedgerSha256=(Pin $ledgerPath);PreviousMarkerSha256=(Pin (Join-Path $Install 'cloudscribe-install.json'));PreviousProvisioningPlanSha256=(Pin (Join-Path $Install 'long-audio-provisioning/plan.json'));PreviousProvisioningReceiptSha256=(Pin (Join-Path $Install 'long-audio-provisioning/receipt.json'));PreviousOperationSha256=$target.Sha256;PreviousAttemptId=$target.AttemptId;NewScopePath=$newPath;NewScopeSha256=(Pin $newPath);RetainedOperationPins=$pins;FundingAdjustment=[ordered]@{PreviousCapUsdCents=$old.TotalGoogleCapUsdCents;NewCapUsdCents=$next.TotalGoogleCapUsdCents}}
 if($stage -ceq 'Complete'){$plan.FundingAdjustment.PreviousTaxFxProvisionUsdCents=$old.TaxFxProvisionUsdCents;$plan.FundingAdjustment.NewTaxFxProvisionUsdCents=$next.TaxFxProvisionUsdCents}
 $planPath=Join-Path $fixtureInput 'plan.json';Write-LAAtomic $planPath (ConvertTo-LABytes $plan)
 $args=@{InstallRoot=$Install;ProfileRoot=$Profile;ReviewedPlanPath=$planPath;ReviewedPlanSha256=(Pin $planPath)}
 $valid=& $writer @args -ValidateOnly
 if(-not $valid.Valid -or $valid.AdditionalApprovedClaims -ne 0){throw 'SyntheticFundingValidation'};$checks++
 foreach($fault in @('string','decimal','bool','plan-cap','unchanged-approval','requests','replacement-history','missing-pin','pending')){
  $bad=ConvertFrom-LAJson (Read-LABytes $planPath);$scope=ConvertFrom-LAJson (Read-LABytes $newPath);$restorePath=$null;$restoreBytes=$null
  try{
   switch($fault){
    'string' {$scope.TotalGoogleCapUsdCents=[string]$scope.TotalGoogleCapUsdCents}
    'decimal' {$scope.TotalGoogleCapUsdCents=[decimal]$scope.TotalGoogleCapUsdCents+0.5}
    'bool' {$scope.TotalGoogleCapUsdCents=$true}
    'plan-cap' {$bad.FundingAdjustment.NewCapUsdCents++}
    'unchanged-approval' {$scope.SourceApprovalSha256=$old.SourceApprovalSha256;$bad.SourceApprovalSha256=$old.SourceApprovalSha256}
    'requests' {$scope.ApprovedRequestCeilingsUsdCents['a'*64]=1}
    'replacement-history' {$scope.FailedRangeReplacements=@()}
    'missing-pin' {$bad.RetainedOperationPins=@($pins|Select-Object -Skip 1)}
    'pending' {$restorePath=Join-Path $data ('operations/'+([Guid]$target.AttemptId).ToString('N')+'.json');$restoreBytes=Read-LABytes $restorePath;$op=ConvertFrom-LAJson $restoreBytes;$op.State=1;Write-LAAtomic $restorePath (ConvertTo-LABytes $op) -Replace;$bad.RetainedOperationPins[0].Sha256=Pin $restorePath;$bad.PreviousOperationSha256=Pin $restorePath}
   }
   $bp=Join-Path $fixtureInput ($fault+'-scope.json');Write-LAAtomic $bp (ConvertTo-LABytes $scope);$bad.NewScopePath=$bp;$bad.NewScopeSha256=Pin $bp
   $pp=Join-Path $fixtureInput ($fault+'-plan.json');Write-LAAtomic $pp (ConvertTo-LABytes $bad)
   $rejected=$false;try{& $writer -InstallRoot $Install -ProfileRoot $Profile -ReviewedPlanPath $pp -ReviewedPlanSha256 (Pin $pp) -ValidateOnly|Out-Null}catch{$rejected=$true}
   if(-not $rejected){throw ('SyntheticFundingAcceptedFault '+$fault)};$checks++
  }finally{if($restorePath){Write-LAAtomic $restorePath $restoreBytes -Replace}}
 }
 $stop={param($at)if($at -ceq $stage){throw 'SyntheticFundingInterrupted'}}.GetNewClosure()
 try{& $writer @args -AfterCheckpoint $stop|Out-Null;throw 'ExpectedInterruption'}catch{if($_.Exception.Message -notlike '*SyntheticFundingInterrupted*'){throw}}
 & $writer @args -Resume|Out-Null
 $receipt=Join-Path $Install 'long-audio-provisioning/receipt.json'
 if((ConvertFrom-LAJson (Read-LABytes $receipt)).Phase -cne 'Complete'){throw 'FundingIncomplete'}
 $after=ConvertFrom-LAJson (Read-LABytes $ledgerPath)
 if((Get-LAHash (ConvertTo-LABytes @($after.Claims))) -cne $claimsSha -or $after.ScopeSha256 -cne $plan.NewScopeSha256){throw 'FundingLostHistory'}
 $checks++
}
[pscustomobject]@{SyntheticFundingVerified=$true;Checks=$checks;RetainedClaims=$initial.Claims.Count;ProviderCalls=0;NewCommitments=0}
