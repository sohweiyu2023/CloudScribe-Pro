param(
 [Parameter(Mandatory)][string]$ReviewedPlanPath,
 [Parameter(Mandatory)][string]$ReviewedPlanSha256,
 [ValidateSet('Inspect','Archive','Rollback')][string]$Phase='Inspect',
 [scriptblock]$AfterCheckpoint
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../tools/long-audio/Provisioning.Files.ps1')
function Exact([string]$Path,[string]$Sha){Assert-LAHash $Sha;$bytes=Read-LABytes $Path;if((Get-LAHash $bytes) -cne $Sha){throw 'UnadmittedPinnedFileChanged'};return ,$bytes}
function Inventory([string]$Root,[string[]]$Exclude=@(),[switch]$Recursive){
 Assert-LAPhysical $Root;$rows=@()
 $entries=if($Recursive){Get-ChildItem -LiteralPath $Root -Force -Recurse}else{Get-ChildItem -LiteralPath $Root -Force}
 foreach($entry in @($entries|Sort-Object FullName)){
  Assert-LAPhysical $entry.FullName
  $name=[IO.Path]::GetRelativePath($Root,$entry.FullName)
  if($entry.PSIsContainer){if(-not $Recursive){throw 'UnadmittedUnexpectedEvidenceDirectory'};$rows+=,[ordered]@{Name=$name;Directory=$true};continue}
  if($Exclude -ccontains $entry.Name){continue}
  if($entry.Name.EndsWith('.lock',[StringComparison]::Ordinal)) {if($entry.Length -ne 0){throw 'UnadmittedChangedLock'};continue}
  $rows+=,[ordered]@{Name=$name;Length=$entry.Length;Sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
 }
 if($rows.Count -eq 0){return Get-LAHash ([Text.Encoding]::UTF8.GetBytes('[]'))}
 return Get-LAHash (ConvertTo-LABytes @($rows))
}
$planBytes=Exact $ReviewedPlanPath $ReviewedPlanSha256;$plan=ConvertFrom-LAJson $planBytes
Assert-LARecord $plan @('Version','InstallRoot','ProfileRoot','AttemptId','BindingSha256','AppHostSha256','AppSha256','InfrastructureSha256','MarkerSha256','ScopeSha256','LedgerSha256','JournalSha256','GuardSha256','OtherOperationsSha256','OutputInventorySha256','SourceApprovalSha256','Reason')
Assert-LAInteger $plan.Version 1
if($plan.Version -ne 1 -or $plan.Reason -cne 'VerifiedBudgetRejectionBeforeDurableAdmission'){throw 'UnadmittedExplicitReviewedReasonRequired'}
foreach($name in @('BindingSha256','AppHostSha256','AppSha256','InfrastructureSha256','MarkerSha256','ScopeSha256','LedgerSha256','JournalSha256','GuardSha256','OtherOperationsSha256','OutputInventorySha256','SourceApprovalSha256')){Assert-LAHash $plan[$name]}
$install=[IO.Path]::GetFullPath($plan.InstallRoot);$profile=[IO.Path]::GetFullPath($plan.ProfileRoot)
if(-not [IO.Path]::IsPathFullyQualified($plan.InstallRoot) -or -not [IO.Path]::IsPathFullyQualified($plan.ProfileRoot) -or $install -eq $profile){throw 'UnadmittedAbsoluteDistinctRootsRequired'}
if(($install.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar).StartsWith(($profile.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase) -or ($profile.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar).StartsWith(($install.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)){throw 'UnadmittedOverlappingRoots'}
Assert-LAPhysical $install;Assert-LAPhysical $profile;Assert-LAClosed $install
$data=Join-Path $profile 'data/google-long-audio';$ops=Join-Path $data 'operations';$output=Join-Path $data 'output'
$id=[Guid]$plan.AttemptId;if($id -eq [Guid]::Empty){throw 'UnadmittedAttemptRequired'};$stem=$id.ToString('N')
$journal=Join-Path $ops ($stem+'.json');$guard=Join-Path $ops 'observation-pending.guard';$ledger=Join-Path $data 'budget-ledger.json'
$history=Join-Path $install ('long-audio-unadmitted-history/'+$ReviewedPlanSha256);Assert-LAPhysical $history
$leases=[Collections.Generic.List[IDisposable]]::new()
try{
 # The instance lease proves this profile is closed even when process discovery is unavailable.
 foreach($path in @((Join-Path $profile '.instance.lock'),(Join-Path $profile '.whole-article-worker.lock'),(Join-Path $install 'long-audio-provisioning.lock'),(Join-Path $ops 'active-response.lock'),($ledger+'.lock'),(Join-Path $ops 'submission.lock'),(Join-Path $ops ($stem+'.lock')))){
  Assert-LAPhysical $path;$leases.Add([IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None))
 }
 [void](Exact (Join-Path $install 'app/CloudScribe.exe') $plan.AppHostSha256)
 [void](Exact (Join-Path $install 'app/CloudScribe.dll') $plan.AppSha256)
 [void](Exact (Join-Path $install 'app/CloudScribe.Infrastructure.dll') $plan.InfrastructureSha256)
 $marker=ConvertFrom-LAJson (Exact (Join-Path $install 'cloudscribe-install.json') $plan.MarkerSha256)
 if([IO.Path]::GetFullPath($marker.Profile).TrimEnd('\','/') -ine $profile.TrimEnd('\','/') -or $marker.AppSha256 -cne $plan.AppSha256 -or $marker.GoogleLongAudioScopeSha256 -cne $plan.ScopeSha256 -or $marker.ContainsKey('LongAudioBuildRebindPending')){throw 'UnadmittedInstalledProfileMismatch'}
 [void](Exact (Join-Path $install 'google-long-audio-scope.json') $plan.ScopeSha256)
 $ledgerBytes=Exact $ledger $plan.LedgerSha256;$book=ConvertFrom-LAJson $ledgerBytes
 Assert-LAInteger $book.Version 1
 if($book.Version -ne 1 -or $book.Claims -isnot [array] -or $book.ScopeSha256 -cne $plan.ScopeSha256 -or @($book.Claims|Where-Object{[Guid]$_.AttemptId -eq $id -or $_.RequestBindingSha256 -ceq $plan.BindingSha256}).Count -ne 0){throw 'UnadmittedAttemptHasCommitmentOrChangedScope'}
 $claimIds=[Collections.Generic.HashSet[Guid]]::new();$bindings=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($claim in $book.Claims){Assert-LAHash $claim.RequestBindingSha256;Assert-LAInteger $claim.MaximumUsdCents 1;if([Guid]$claim.AttemptId -eq [Guid]::Empty -or -not $claimIds.Add([Guid]$claim.AttemptId) -or -not $bindings.Add($claim.RequestBindingSha256)){throw 'UnadmittedInvalidRetainedClaims'}}
 if((Inventory $ops @(($stem+'.json'),'observation-pending.guard')) -cne $plan.OtherOperationsSha256 -or (Inventory $output -Recursive) -cne $plan.OutputInventorySha256){throw 'UnadmittedOtherEvidenceChanged'}
 $seen=[Collections.Generic.HashSet[Guid]]::new()
 foreach($entry in @(Get-ChildItem -LiteralPath $ops -Filter '*.json')){
  if($entry.Name -ceq $stem+'.json'){continue};$other=ConvertFrom-LAJson (Read-LABytes $entry.FullName)
  $otherId=[Guid]$other.AttemptId;$claim=@($book.Claims|Where-Object{[Guid]$_.AttemptId -eq $otherId})
  if(-not $claimIds.Contains($otherId) -or -not $seen.Add($otherId) -or $claim.Count -ne 1 -or $entry.Name -cne ($otherId.ToString('N')+'.json') -or $other.BindingSha256 -cne $claim[0].RequestBindingSha256 -or $other.State -notin @(2,3) -or $other.OperationParseFailed -or -not $other.Observation.ReadbackVerified -or $other.Observation.Truncated){throw 'UnadmittedOtherOutcomeUnresolved'}
 }
 if($seen.Count -ne $claimIds.Count){throw 'UnadmittedRetainedOperationMissing'}
 $archiveJournal=Join-Path $history 'original-journal.json';$archiveGuard=Join-Path $history 'original-guard.bin';$receiptPath=Join-Path $history 'receipt.json'
 $journalBytes=if(Test-Path -LiteralPath $journal){Exact $journal $plan.JournalSha256}else{Exact $archiveJournal $plan.JournalSha256}
 $guardBytes=if(Test-Path -LiteralPath $guard){Exact $guard $plan.GuardSha256}else{Exact $archiveGuard $plan.GuardSha256}
 $record=ConvertFrom-LAJson $journalBytes
 foreach($field in @('State','Revision','HttpStatus','FailureKind')){Assert-LAInteger $record[$field]}
 if([Guid]$record.AttemptId -ne $id -or $record.BindingSha256 -cne $plan.BindingSha256 -or $record.State -ne 0 -or $record.Revision -ne 0 -or $record.HttpStatus -ne 0 -or $null -ne $record.OperationName -or $null -ne $record.RpcCode -or $null -ne $record.Observation -or $record.OperationParseFailed -or $record.FailureKind -ne 0 -or [Text.Encoding]::ASCII.GetString($guardBytes) -cne $stem){throw 'UnadmittedRecordIsNotPristinePreDispatch'}
 foreach($entry in @(Get-ChildItem -LiteralPath $output -Force)){if($entry.Name.StartsWith($stem,[StringComparison]::OrdinalIgnoreCase)){throw 'UnadmittedAttemptHasOutputEvidence'}}
 $liveJournal=Test-Path -LiteralPath $journal;$liveGuard=Test-Path -LiteralPath $guard;$receipt=$null
 if(Test-Path -LiteralPath $receiptPath){
  $receipt=ConvertFrom-LAJson (Read-LABytes $receiptPath)
  Assert-LARecord $receipt @('Version','PlanSha256','Phase','NoDurableAdmission','LedgerSha256')
  Assert-LAInteger $receipt.Version 1
  if($receipt.Version -ne 1 -or $receipt.NoDurableAdmission -isnot [bool] -or -not $receipt.NoDurableAdmission -or $receipt.Phase -cnotin @('Prepared','JournalArchived','Complete','Restoring','RolledBack') -or $receipt.PlanSha256 -cne $ReviewedPlanSha256 -or $receipt.LedgerSha256 -cne $plan.LedgerSha256){throw 'UnadmittedRecoveryReceiptChanged'}
  if(($receipt.Phase -ceq 'Prepared' -and -not $liveGuard) -or ($receipt.Phase -ceq 'JournalArchived' -and $liveJournal) -or ($receipt.Phase -ceq 'Complete' -and ($liveJournal -or $liveGuard)) -or ($receipt.Phase -ceq 'RolledBack' -and (-not $liveJournal -or -not $liveGuard))){throw 'UnadmittedReceiptContradictsLiveState'}
  if($receipt.Phase -ceq 'Restoring' -and $Phase -ceq 'Archive'){throw 'UnadmittedFinishExistingRollbackFirst'}
 }elseif(-not $liveJournal -or -not $liveGuard){throw 'UnadmittedMissingRecoveryReceipt'}
 if($Phase -eq 'Inspect'){[pscustomobject]@{Valid=$true;AttemptId=$id;LiveWrites=0;ProviderCalls=0;RetainedClaims=$book.Claims.Count;NextAction='Explicit reviewed archive; does not renew funding'};return}
 if($Phase -ceq 'Archive' -and $receipt -and $receipt.Phase -ceq 'Complete'){[pscustomobject]@{Phase='Complete';History=$history;LedgerUnchanged=$true;ProviderCalls=0;CapChanged=$false};return}
 if(-not(Test-Path -LiteralPath $history)){[void][IO.Directory]::CreateDirectory($history)}
 Set-LAExactFile (Join-Path $history 'reviewed-plan.json') $planBytes
 Set-LAExactFile $archiveJournal $journalBytes;Set-LAExactFile $archiveGuard $guardBytes;Set-LAExactFile (Join-Path $history 'ledger-before.json') $ledgerBytes
 function Checkpoint([string]$Value){
  Write-LAAtomic $receiptPath (ConvertTo-LABytes ([ordered]@{Version=1;PlanSha256=$ReviewedPlanSha256;Phase=$Value;NoDurableAdmission=$true;LedgerSha256=$plan.LedgerSha256})) -Replace
  if($AfterCheckpoint){& $AfterCheckpoint $Value}
 }
 if($Phase -eq 'Rollback'){
  # Exact snapshots and inventory checks above reject any later request or changed output.
  Checkpoint 'Restoring'
  Set-LAExactFile $journal $journalBytes;Set-LAExactFile $guard $guardBytes;Checkpoint 'RolledBack'
 }else{
  if(-not $receipt -or $receipt.Phase -ceq 'RolledBack'){Checkpoint 'Prepared'}
  if(Test-Path -LiteralPath $journal){[void](Exact $journal $plan.JournalSha256);[IO.File]::Delete($journal)}
  Checkpoint 'JournalArchived'
  if(Test-Path -LiteralPath $guard){[void](Exact $guard $plan.GuardSha256);[IO.File]::Delete($guard)}
  Checkpoint 'Complete'
 }
 [void](Exact $ledger $plan.LedgerSha256)
 [pscustomobject]@{Phase=$Phase;History=$history;LedgerUnchanged=$true;RetainedClaims=$book.Claims.Count;ProviderCalls=0;CapChanged=$false}
}finally{for($n=$leases.Count-1;$n -ge 0;$n--){$leases[$n].Dispose()}}
