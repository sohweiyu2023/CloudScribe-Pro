param([Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'tools/long-audio/Provisioning.Files.ps1')
. (Join-Path $Repository 'tools/long-audio/Funding.Transition.ps1')
$checks=0
foreach($fault in @('valid','scope-string','scope-decimal','scope-bool','plan-string','missing-old','missing-new','wrong-old','wrong-new','decreased','unchanged','cap-reserve','unapproved-reserve','other-field')){
 $old=[ordered]@{TotalGoogleCapUsdCents=2000;TaxFxProvisionUsdCents=200;SourceApprovalSha256=('a'*64);History=@('retained')}
 $next=[ordered]@{TotalGoogleCapUsdCents=5000;TaxFxProvisionUsdCents=650;SourceApprovalSha256=('b'*64);History=@('retained')}
 $plan=[ordered]@{SourceApprovalSha256=('b'*64);FundingAdjustment=[ordered]@{PreviousCapUsdCents=2000;NewCapUsdCents=5000;PreviousTaxFxProvisionUsdCents=200;NewTaxFxProvisionUsdCents=650}}
 switch($fault){
  'scope-string' {$next.TaxFxProvisionUsdCents='650'}
  'scope-decimal' {$next.TaxFxProvisionUsdCents=[decimal]650.0}
  'scope-bool' {$next.TaxFxProvisionUsdCents=$true}
  'plan-string' {$plan.FundingAdjustment.NewTaxFxProvisionUsdCents='650'}
  'missing-old' {$plan.FundingAdjustment.Remove('PreviousTaxFxProvisionUsdCents')}
  'missing-new' {$plan.FundingAdjustment.Remove('NewTaxFxProvisionUsdCents')}
  'wrong-old' {$plan.FundingAdjustment.PreviousTaxFxProvisionUsdCents=100}
  'wrong-new' {$plan.FundingAdjustment.NewTaxFxProvisionUsdCents=600}
  'decreased' {$next.TaxFxProvisionUsdCents=100;$plan.FundingAdjustment.NewTaxFxProvisionUsdCents=100}
  'unchanged' {$next.TaxFxProvisionUsdCents=200;$plan.FundingAdjustment.NewTaxFxProvisionUsdCents=200}
  'cap-reserve' {$next.TaxFxProvisionUsdCents=5000;$plan.FundingAdjustment.NewTaxFxProvisionUsdCents=5000}
  'unapproved-reserve' {$plan.FundingAdjustment.Remove('PreviousTaxFxProvisionUsdCents');$plan.FundingAdjustment.Remove('NewTaxFxProvisionUsdCents')}
  'other-field' {$next.History=@('replaced')}
 }
 $rejected=$false;try{Assert-LAFundingAdjustment $old $next $plan}catch{$rejected=$true}
 if($rejected -ne ($fault -cne 'valid')){throw ('Funding guard mismatch: '+$fault)};$checks++
}
[pscustomobject]@{Passed=$checks;ProviderCalls=0;LiveProfileAccess=0;NewCommitments=0}
