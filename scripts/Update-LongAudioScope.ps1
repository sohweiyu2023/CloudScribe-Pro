[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallRoot,[Parameter(Mandatory)][string]$ProfileRoot,
 [Parameter(Mandatory)][string]$ReviewedPlanPath,[Parameter(Mandatory)][string]$ReviewedPlanSha256,
 [switch]$ValidateOnly,[switch]$Resume,[scriptblock]$AfterCheckpoint)
$ErrorActionPreference='Stop'
$formatValidateOnly=$ValidateOnly;$formatResume=$Resume
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$ValidateOnly=$formatValidateOnly;$Resume=$formatResume
$helpers=Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/long-audio'
foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1')){. (Join-Path $helpers $name)}
Assert-LAHash $ReviewedPlanSha256
$planBytes=Read-LABytes $ReviewedPlanPath
if((Get-LAHash $planBytes) -cne $ReviewedPlanSha256){throw 'LongAudioSuccessorPlanPin'}
$plan=ConvertFrom-LAJson $planBytes
Assert-LARecord $plan @('Version','SourceApprovalSha256','PreviousScopeSha256','PreviousLedgerSha256','PreviousMarkerSha256','PreviousProvisioningPlanSha256','PreviousProvisioningReceiptSha256','PreviousOperationSha256','PreviousAttemptId','NewScopePath','NewScopeSha256')
if($plan.Version -ne 1){throw 'LongAudioSuccessorVersion'}
foreach($key in $plan.Keys|Where-Object {$_ -like '*Sha256'}){Assert-LAHash $plan[$key]}
Assert-LAPhysical $InstallRoot;Assert-LAPhysical $ProfileRoot
$install=[IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$profile=[IO.Path]::GetFullPath($ProfileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$history=Join-Path $install ('long-audio-scope-history/'+$plan.NewScopeSha256)
Assert-LAPhysical $history
function Exact([string]$Path,[string]$Hash){$bytes=Read-LABytes $Path;if((Get-LAHash $bytes) -cne $Hash){throw 'LongAudioSuccessorEvidenceChanged'};return ,$bytes}
function Inspect-Transition {
 $sealed=Test-Path -LiteralPath (Join-Path $history 'transition-plan.json')
 if($sealed){$null=Exact (Join-Path $history 'transition-plan.json') $ReviewedPlanSha256}
 $oldRoot=if($sealed){$history}else{$install}
 $oldScope=ConvertFrom-LAJson (Exact (Join-Path $oldRoot 'google-long-audio-scope.json') $plan.PreviousScopeSha256)
 $oldLedgerPath=if($sealed){Join-Path $history 'budget-ledger.json'}else{Join-Path $profile 'data/google-long-audio/budget-ledger.json'}
 $oldLedger=ConvertFrom-LAJson (Exact $oldLedgerPath $plan.PreviousLedgerSha256)
 $oldMarker=ConvertFrom-LAJson (Exact (Join-Path $oldRoot 'cloudscribe-install.json') $plan.PreviousMarkerSha256)
 $oldProvision=if($sealed){Join-Path $history 'predecessor-provisioning'}else{Join-Path $install 'long-audio-provisioning'}
 if(-not (Test-Path -LiteralPath $oldProvision)){ $oldProvision=Join-Path $history 'predecessor-provisioning-copy' }
 $null=Exact (Join-Path $oldProvision 'plan.json') $plan.PreviousProvisioningPlanSha256
 $receipt=ConvertFrom-LAJson (Exact (Join-Path $oldProvision 'receipt.json') $plan.PreviousProvisioningReceiptSha256)
 if($receipt.Phase -cne 'Complete' -or $receipt.PlanSha256 -cne $plan.PreviousProvisioningPlanSha256){throw 'LongAudioSuccessorPredecessorIncomplete'}
 $parentPath=Join-Path $install 'chirp-article-admission-policy.json'
 $parentBytes=Exact $parentPath $oldScope.ParentPolicySha256;$parent=ConvertFrom-LAJson $parentBytes
 $parentJson=[Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false,$true).GetString($parentBytes),[Text.Json.JsonDocumentOptions]@{MaxDepth=64})
 try{Assert-ChirpPolicy $parentJson.RootElement}finally{$parentJson.Dispose()}
 $canonical=Get-LAHash ([Text.Encoding]::UTF8.GetBytes($profile.ToUpperInvariant()))
 if($parent.ProfileRootSha256 -cne $canonical -or $oldMarker.Profile -cne $profile -or $oldMarker.ChirpArticlePolicySha256 -cne $oldScope.ParentPolicySha256 -or
  (Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.dll') 536870912)) -cne $parent.AppSha256 -or
  (Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.Infrastructure.dll') 536870912)) -cne $parent.InfrastructureSha256){throw 'LongAudioSuccessorInstalledIdentity'}
 # This narrow transition handles the first definitively rejected format trial only; it is not a reset or generic retry command.
 Assert-LAScope $oldScope $parent $oldScope.ParentPolicySha256
 if($oldLedger.Version -ne 1 -or $oldLedger.ScopeSha256 -cne $plan.PreviousScopeSha256 -or $oldLedger.Claims.Count -ne 1){throw 'LongAudioSuccessorExactRetainedClaim'}
 $claim=$oldLedger.Claims[0]
 if([Guid]$claim.AttemptId -ne [Guid]$plan.PreviousAttemptId -or $oldScope.ApprovedRequestCeilingsUsdCents.Count -ne 1 -or
  -not $oldScope.ApprovedRequestCeilingsUsdCents.Contains($claim.RequestBindingSha256) -or $oldScope.ApprovedRequestCeilingsUsdCents[$claim.RequestBindingSha256] -ne $claim.MaximumUsdCents){throw 'LongAudioSuccessorRetainedAmount'}
 $operations=Join-Path $profile 'data/google-long-audio/operations';Assert-LAPhysical $operations
 $journals=@(Get-ChildItem -LiteralPath $operations -File -Filter '*.json')
 if($journals.Count -ne 1){throw 'LongAudioSuccessorUnexpectedOperations'}
 foreach($entry in @(Get-ChildItem -LiteralPath $operations -Force)){
  Assert-LAPhysical $entry.FullName
  if($entry.PSIsContainer -or ($entry.FullName -cne $journals[0].FullName -and (-not $entry.Name.EndsWith('.lock',[StringComparison]::Ordinal) -or $entry.Length -ne 0))){throw 'LongAudioSuccessorUnrecognizedOperationEvidence'}
 }
 $oldOperation=ConvertFrom-LAJson (Exact $journals[0].FullName $plan.PreviousOperationSha256)
 if([Guid]$oldOperation.AttemptId -ne [Guid]$claim.AttemptId -or $oldOperation.BindingSha256 -cne $claim.RequestBindingSha256 -or $oldOperation.State -ne 3 -or
  $null -ne $oldOperation.OperationName -or $oldOperation.HttpStatus -ne 400 -or $oldOperation.OperationParseFailed -or -not $oldOperation.Observation.ReadbackVerified -or
  $oldOperation.Observation.Truncated -or $oldOperation.Observation.DetailsOmitted){throw 'LongAudioSuccessorNotDefinitivelyRejected'}
 $newBytes=Exact $plan.NewScopePath $plan.NewScopeSha256;$scope=ConvertFrom-LAJson $newBytes
 if($scope.SourceApprovalSha256 -cne $plan.SourceApprovalSha256 -or $scope.SourceApprovalSha256 -ceq $oldScope.SourceApprovalSha256 -or $scope.ParentPolicySha256 -cne $oldScope.ParentPolicySha256 -or
  $scope.ProjectNumber -cne $oldScope.ProjectNumber -or $scope.CredentialReferenceId -cne $oldScope.CredentialReferenceId -or
  $scope.VoiceName -cne $oldScope.VoiceName -or $scope.LanguageCode -cne $oldScope.LanguageCode -or $scope.OutputBucket -cne $oldScope.OutputBucket -or
  $oldScope.AudioEncoding -cne 'MP3' -or $scope.AudioEncoding -cne 'LINEAR16' -or $scope.TotalGoogleCapUsdCents -ne $oldScope.TotalGoogleCapUsdCents -or
  $scope.TaxFxProvisionUsdCents -ne $oldScope.TaxFxProvisionUsdCents -or $scope.UsdPerMillionCharacters -ne $oldScope.UsdPerMillionCharacters -or
  $scope.ApprovedRequestCeilingsUsdCents.Count -ne 2 -or $scope.DocumentJobs.Count -ne 1){throw 'LongAudioSuccessorScopeChange'}
 if(-not $scope.ApprovedRequestCeilingsUsdCents.Contains($claim.RequestBindingSha256) -or $scope.ApprovedRequestCeilingsUsdCents[$claim.RequestBindingSha256] -ne $claim.MaximumUsdCents){throw 'LongAudioSuccessorHistoryOmitted'}
 $newJob=$scope.DocumentJobs[0];$oldJob=$oldScope.DocumentJobs[0]
 foreach($key in @('DocumentId','RevisionId','SourceSha256','InputMode')){if($newJob[$key] -cne $oldJob[$key]){throw 'LongAudioSuccessorSourceChange'}}
 if([Guid]$newJob.AttemptId -eq [Guid]$claim.AttemptId -or (Test-Path -LiteralPath (Join-Path $operations (([Guid]$newJob.AttemptId).ToString('N')+'.json')))){throw 'LongAudioSuccessorAttemptReplay'}
 $currentOnly=ConvertFrom-LAJson $newBytes;[void]$currentOnly.ApprovedRequestCeilingsUsdCents.Remove($claim.RequestBindingSha256)
 Assert-LAScope $currentOnly $parent $scope.ParentPolicySha256
 $context=@{Scope=$currentOnly;Profile=$profile};Assert-LASavedInputs $context
 [decimal]$provision=[decimal]$parent.Budget.ProjectCeilingMinorUnits+[decimal]$scope.TaxFxProvisionUsdCents
 foreach($amount in $scope.ApprovedRequestCeilingsUsdCents.Values){$provision += [decimal]$amount}
 if($provision -gt $scope.TotalGoogleCapUsdCents){throw 'LongAudioSuccessorBudget'}
 $marker=ConvertFrom-LAJson (ConvertTo-LABytes $oldMarker);$marker.GoogleLongAudioScopeSha256=$plan.NewScopeSha256
 $ledger=[ordered]@{Version=1;ScopeSha256=$plan.NewScopeSha256;Claims=@($oldLedger.Claims)}
 return @{ScopeBytes=$newBytes;LedgerBytes=(ConvertTo-LABytes $ledger);MarkerBytes=(ConvertTo-LABytes $marker);OldRoot=$oldRoot;OldProvision=$oldProvision;Parent=$parent;Provision=$provision;Sealed=$sealed}
}
# The actual old ledger is at the profile root, not the install root. Keep an immutable read lease before capturing it.
function Inspect-LiveOrSealed {
 if(Test-Path -LiteralPath (Join-Path $history 'transition-plan.json')){return Inspect-Transition}
 # Read the predecessor inputs directly without altering live files.
 $script:liveLedgerPath=Join-Path $profile 'data/google-long-audio/budget-ledger.json'
 $null=Exact $script:liveLedgerPath $plan.PreviousLedgerSha256
 # Inspect-Transition takes an explicit in-memory path override for this single predecessor input.
 return Inspect-Transition
}
$context=Inspect-LiveOrSealed
if($ValidateOnly){[pscustomobject]@{Valid=$true;RetainedClaims=1;AdditionalApprovedClaims=1;TotalProvisionCents=$context.Provision;ProviderCalls=0};return}
Assert-LAClosed $install
$instancePath=Join-Path $profile '.instance.lock';Assert-LAPhysical $instancePath
$instance=[IO.FileStream]::new($instancePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $installLeasePath=Join-Path $install 'long-audio-provisioning.lock';Assert-LAPhysical $installLeasePath
 $lease=[IO.FileStream]::new($installLeasePath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{
  $budgetLeasePath=Join-Path $profile 'data/google-long-audio/budget-ledger.json.lock';Assert-LAPhysical $budgetLeasePath
  $budgetLease=[IO.FileStream]::new($budgetLeasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  try{
   $context=Inspect-LiveOrSealed
   if($context.Sealed -and -not $Resume){throw 'LongAudioSuccessorUseExactResume'}
   if(-not $context.Sealed){
    [IO.Directory]::CreateDirectory($history)|Out-Null
    Set-LAExactFile (Join-Path $history 'google-long-audio-scope.json') (Exact (Join-Path $install 'google-long-audio-scope.json') $plan.PreviousScopeSha256)
    Set-LAExactFile (Join-Path $history 'budget-ledger.json') (Exact (Join-Path $profile 'data/google-long-audio/budget-ledger.json') $plan.PreviousLedgerSha256)
    Set-LAExactFile (Join-Path $history 'cloudscribe-install.json') (Exact (Join-Path $install 'cloudscribe-install.json') $plan.PreviousMarkerSha256)
    $copy=Join-Path $history 'predecessor-provisioning-copy';[IO.Directory]::CreateDirectory($copy)|Out-Null
    foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $install 'long-audio-provisioning') -File)){Assert-LAPhysical $file.FullName;Set-LAExactFile (Join-Path $copy $file.Name) (Read-LABytes $file.FullName)}
    Set-LAExactFile (Join-Path $history 'transition-plan.json') $planBytes
   }
   $context=Inspect-LiveOrSealed
   foreach($entry in @(@((Join-Path $install 'google-long-audio-scope.json'),$plan.PreviousScopeSha256,(Get-LAHash $context.ScopeBytes)),@((Join-Path $profile 'data/google-long-audio/budget-ledger.json'),$plan.PreviousLedgerSha256,(Get-LAHash $context.LedgerBytes)),@((Join-Path $install 'cloudscribe-install.json'),$plan.PreviousMarkerSha256,(Get-LAHash $context.MarkerBytes)))){
    if((Get-LAHash (Read-LABytes $entry[0])) -cnotin @($entry[1],$entry[2])){throw 'LongAudioSuccessorUnexpectedLiveBytes'}
   }
   $current=Join-Path $install 'long-audio-provisioning';$predecessor=Join-Path $history 'predecessor-provisioning'
   if(-not (Test-Path -LiteralPath $predecessor)){Assert-LAPhysical $current;Assert-LAPhysical $predecessor;[IO.Directory]::Move($current,$predecessor)}
   if($AfterCheckpoint){& $AfterCheckpoint 'PredecessorArchived'}
   [IO.Directory]::CreateDirectory($current)|Out-Null
   $sealPath=Join-Path $history 'new-plan.json'
   if(Test-Path -LiteralPath $sealPath){$newPlanBytes=Read-LABytes $sealPath;$newPlan=ConvertFrom-LAJson $newPlanBytes}
   else{
    $newPlan=[ordered]@{Version=1;TransactionId=[Guid]::NewGuid().ToString('D');ScopeSha256=$plan.NewScopeSha256;ParentPolicySha256=(ConvertFrom-LAJson $context.ScopeBytes).ParentPolicySha256;ProfileRootSha256=$context.Parent.ProfileRootSha256;AppSha256=$context.Parent.AppSha256;InfrastructureSha256=$context.Parent.InfrastructureSha256;PreviousMarkerSha256=$plan.PreviousMarkerSha256;NewMarkerSha256=(Get-LAHash $context.MarkerBytes);InitialLedgerSha256=(Get-LAHash $context.LedgerBytes);CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    $newPlanBytes=ConvertTo-LABytes $newPlan;Set-LAExactFile $sealPath $newPlanBytes
   }
   if($newPlan.Version -ne 1 -or [Guid]$newPlan.TransactionId -eq [Guid]::Empty -or $newPlan.ScopeSha256 -cne $plan.NewScopeSha256 -or
    $newPlan.ParentPolicySha256 -cne (ConvertFrom-LAJson $context.ScopeBytes).ParentPolicySha256 -or $newPlan.ProfileRootSha256 -cne $context.Parent.ProfileRootSha256 -or
    $newPlan.AppSha256 -cne $context.Parent.AppSha256 -or $newPlan.InfrastructureSha256 -cne $context.Parent.InfrastructureSha256 -or
    $newPlan.PreviousMarkerSha256 -cne $plan.PreviousMarkerSha256 -or $newPlan.NewMarkerSha256 -cne (Get-LAHash $context.MarkerBytes) -or
    $newPlan.InitialLedgerSha256 -cne (Get-LAHash $context.LedgerBytes)){throw 'LongAudioSuccessorNewPlanChanged'}
   Set-LAExactFile (Join-Path $current 'plan.json') $newPlanBytes
   foreach($entry in @(@('scope.json',$context.ScopeBytes),@('ledger-initial.json',$context.LedgerBytes),@('marker-before.json',(Read-LABytes (Join-Path $history 'cloudscribe-install.json'))),@('marker-after.json',$context.MarkerBytes))){Set-LAExactFile (Join-Path $current $entry[0]) $entry[1]}
   $receipt=[ordered]@{Version=1;TransactionId=$newPlan.TransactionId;PlanSha256=(Get-LAHash $newPlanBytes);Phase='Prepared'}
   Write-LAAtomic (Join-Path $current 'receipt.json') (ConvertTo-LABytes $receipt) -Replace:(Test-Path -LiteralPath (Join-Path $current 'receipt.json'))
   if($AfterCheckpoint){& $AfterCheckpoint 'Prepared'}
   Write-LAAtomic (Join-Path $install 'google-long-audio-scope.json') $context.ScopeBytes -Replace
   if($AfterCheckpoint){& $AfterCheckpoint 'ScopeWritten'}
   Write-LAAtomic (Join-Path $profile 'data/google-long-audio/budget-ledger.json') $context.LedgerBytes -Replace
   if($AfterCheckpoint){& $AfterCheckpoint 'LedgerWritten'}
   Write-LAAtomic (Join-Path $install 'cloudscribe-install.json') $context.MarkerBytes -Replace
   if($AfterCheckpoint){& $AfterCheckpoint 'MarkerWritten'}
   $receipt.Phase='Complete';Write-LAAtomic (Join-Path $current 'receipt.json') (ConvertTo-LABytes $receipt) -Replace
   if($AfterCheckpoint){& $AfterCheckpoint 'Complete'}
   $null=Exact (Join-Path $install 'google-long-audio-scope.json') $plan.NewScopeSha256
   $null=Exact (Join-Path $profile 'data/google-long-audio/budget-ledger.json') (Get-LAHash $context.LedgerBytes)
   $null=Exact (Join-Path $install 'cloudscribe-install.json') (Get-LAHash $context.MarkerBytes)
   $null=Exact (Join-Path $current 'plan.json') (Get-LAHash $newPlanBytes)
   $null=Exact (Join-Path $current 'receipt.json') (Get-LAHash (ConvertTo-LABytes $receipt))
   [pscustomobject]@{Updated=$true;RetainedClaims=1;NewCommitments=0;ProviderCalls=0;TotalProvisionCents=$context.Provision;History=$history}
  }finally{$budgetLease.Dispose()}
 }finally{$lease.Dispose()}
}finally{$instance.Dispose()}
