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
Assert-LARecord $plan @('Version','SourceApprovalSha256','PreviousScopeSha256','PreviousLedgerSha256','PreviousMarkerSha256','PreviousProvisioningPlanSha256','PreviousProvisioningReceiptSha256','PreviousOperationSha256','PreviousAttemptId','NewScopePath','NewScopeSha256','RetainedOperationPins','SourceExcerpt')
if($plan.Version -notin @(2,3)){throw 'LongAudioSuccessorVersion'}
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
 # A distinct explicitly approved source excerpt may follow terminal trials; none of their claims are reset.
 if($oldLedger.Version -ne 1 -or $oldLedger.ScopeSha256 -cne $plan.PreviousScopeSha256 -or $oldLedger.Claims.Count -eq 0 -or
  $oldScope.ApprovedRequestCeilingsUsdCents.Count -ne $oldLedger.Claims.Count){throw 'LongAudioSuccessorExactRetainedClaim'}
 $activeClaims=@($oldLedger.Claims|Where-Object {[Guid]$_.AttemptId -eq [Guid]$plan.PreviousAttemptId})
 if($activeClaims.Count -ne 1 -or [Guid]$oldScope.DocumentJobs[0].AttemptId -ne [Guid]$plan.PreviousAttemptId){throw 'LongAudioSuccessorActivePredecessor'}
 $claim=$activeClaims[0];$ids=[Collections.Generic.HashSet[Guid]]::new();$keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($retained in $oldLedger.Claims){
  Assert-LARecord $retained @('AttemptId','RequestBindingSha256','MaximumUsdCents');Assert-LAHash $retained.RequestBindingSha256;Assert-LAInteger $retained.MaximumUsdCents 1
  if([Guid]$retained.AttemptId -eq [Guid]::Empty -or -not $ids.Add([Guid]$retained.AttemptId) -or -not $keys.Add($retained.RequestBindingSha256) -or
   -not $oldScope.ApprovedRequestCeilingsUsdCents.Contains($retained.RequestBindingSha256) -or $oldScope.ApprovedRequestCeilingsUsdCents[$retained.RequestBindingSha256] -ne $retained.MaximumUsdCents){throw 'LongAudioSuccessorRetainedAmount'}
 }
 $oldCurrent=ConvertFrom-LAJson (ConvertTo-LABytes $oldScope)
 foreach($retained in $oldLedger.Claims){if([Guid]$retained.AttemptId -ne [Guid]$plan.PreviousAttemptId){[void]$oldCurrent.ApprovedRequestCeilingsUsdCents.Remove($retained.RequestBindingSha256)}}
 Assert-LAScope $oldCurrent $parent $oldScope.ParentPolicySha256
 Assert-LASavedInputs @{Scope=$oldCurrent;Profile=$profile}
 $operations=Join-Path $profile 'data/google-long-audio/operations';Assert-LAPhysical $operations
 $journals=@(Get-ChildItem -LiteralPath $operations -File -Filter '*.json')
 if($journals.Count -ne $oldLedger.Claims.Count -or $plan.RetainedOperationPins -isnot [array] -or $plan.RetainedOperationPins.Count -ne $oldLedger.Claims.Count){throw 'LongAudioSuccessorUnexpectedOperations'}
 foreach($entry in @(Get-ChildItem -LiteralPath $operations -Force)){
  Assert-LAPhysical $entry.FullName
  if($entry.PSIsContainer -or ($entry.Extension -cne '.json' -and (-not $entry.Name.EndsWith('.lock',[StringComparison]::Ordinal) -or $entry.Length -ne 0))){throw 'LongAudioSuccessorUnrecognizedOperationEvidence'}
 }
 $pinned=[Collections.Generic.HashSet[Guid]]::new()
 foreach($pin in $plan.RetainedOperationPins){
  if($plan.Version -eq 2 -or -not $pin.ContainsKey('CompletedReceiptSha256')){Assert-LARecord $pin @('AttemptId','Sha256')}
  else{Assert-LARecord $pin @('AttemptId','Sha256','CompletedReceiptSha256','CompletedObjectSha256')}
  Assert-LAHash $pin.Sha256
  if(-not $ids.Contains([Guid]$pin.AttemptId) -or -not $pinned.Add([Guid]$pin.AttemptId)){throw 'LongAudioSuccessorOperationPinIdentity'}
  $retained=@($oldLedger.Claims|Where-Object {[Guid]$_.AttemptId -eq [Guid]$pin.AttemptId})[0]
  $op=ConvertFrom-LAJson (Exact (Join-Path $operations (([Guid]$pin.AttemptId).ToString('N')+'.json')) $pin.Sha256)
  $terminal=($op.HttpStatus -eq 400 -and $null -eq $op.OperationName) -or ($op.HttpStatus -eq 200 -and $op.RpcCode -eq 3 -and
   $op.OperationName -like ('projects/'+$oldScope.ProjectNumber+'/locations/global/operations/*'))
  $rejected=$op.State -eq 3 -and $terminal
  $completed=$plan.Version -eq 3 -and $op.State -eq 2 -and $op.HttpStatus -eq 200 -and $null -eq $op.RpcCode -and
   $op.OperationName -like ('projects/'+$oldScope.ProjectNumber+'/locations/global/operations/*')
  if([Guid]$op.AttemptId -ne [Guid]$retained.AttemptId -or $op.BindingSha256 -cne $retained.RequestBindingSha256 -or
   (-not $rejected -and -not $completed) -or $op.OperationParseFailed -or -not $op.Observation.ReadbackVerified -or $op.Observation.Truncated){throw 'LongAudioSuccessorNotDefinitivelyTerminal'}
  if($completed){
   $output=Join-Path $profile 'data/google-long-audio/output';Assert-LAPhysical $output
   $stem=([Guid]$retained.AttemptId).ToString('N');$audioPath=Join-Path $output ($stem+'.wav');Assert-LAPhysical $audioPath
   Assert-LAHash $pin.CompletedReceiptSha256;Assert-LAHash $pin.CompletedObjectSha256
   $media=ConvertFrom-LAJson (Exact (Join-Path $output ($stem+'.verified.json')) $pin.CompletedReceiptSha256)
   $object=ConvertFrom-LAJson (Exact (Join-Path $output ($stem+'.object.json')) $pin.CompletedObjectSha256)
   $artifact=$media.Artifact;$identity=$artifact.Identity
   foreach($key in @('BindingSha256','Bucket','ObjectName','Generation','Bytes','Md5Base64')){
    if($identity[$key] -cne $object[$key]){throw 'LongAudioSuccessorObjectIdentityChanged'}
   }
   [long]$generation=0
   if(-not [long]::TryParse($identity.Generation,[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$generation) -or
    $generation -le 0 -or $identity.Generation -cne $generation.ToString([Globalization.CultureInfo]::InvariantCulture) -or
    $identity.Md5Base64.Length -ne 24 -or [Convert]::FromBase64String($identity.Md5Base64).Length -ne 16){throw 'LongAudioSuccessorObjectIdentityChanged'}
   if([Guid]$pin.AttemptId -eq [Guid]$plan.PreviousAttemptId -and ([Guid]$media.DocumentId -ne [Guid]$oldScope.DocumentJobs[0].DocumentId -or
    [Guid]$media.RevisionId -ne [Guid]$oldScope.DocumentJobs[0].RevisionId -or $media.SourceSha256 -cne $oldScope.DocumentJobs[0].SourceSha256)){throw 'LongAudioSuccessorCompletedSourceChanged'}
   $savedSource=Join-Path $profile ('documents/'+([Guid]$media.DocumentId).ToString('N')+'/content/'+([Guid]$media.RevisionId).ToString('N')+'.utf8')
   Assert-LAHash $media.SourceSha256;Assert-LAHash $artifact.AudioSha256;Assert-LAInteger $artifact.Bytes 1
   $null=Exact $savedSource $media.SourceSha256
   if($media.Version -ne 1 -or $media.RequestBindingSha256 -cne $retained.RequestBindingSha256 -or
    $identity.BindingSha256 -cne $retained.RequestBindingSha256 -or $identity.Bucket -cne $oldScope.OutputBucket -or
    ('gs://'+$identity.Bucket+'/'+$identity.ObjectName) -cne $op.OutputGcsUri -or [string]::IsNullOrWhiteSpace($identity.Generation) -or
    [IO.Path]::GetFullPath($artifact.LocalPath) -cne [IO.Path]::GetFullPath($audioPath) -or $identity.Bytes -ne $artifact.Bytes -or
    (Get-Item -LiteralPath $audioPath).Length -ne $artifact.Bytes -or
    (Get-FileHash -LiteralPath $audioPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifact.AudioSha256){throw 'LongAudioSuccessorCompletedOutputChanged'}
   $audioRead=[IO.File]::OpenRead($audioPath)
   try{$md5=[Convert]::ToBase64String([Security.Cryptography.MD5]::HashData($audioRead))}finally{$audioRead.Dispose()}
   if($md5 -cne $identity.Md5Base64){throw 'LongAudioSuccessorCompletedChecksumChanged'}
  }
  if([Guid]$pin.AttemptId -eq [Guid]$plan.PreviousAttemptId -and $pin.Sha256 -cne $plan.PreviousOperationSha256){throw 'LongAudioSuccessorActiveOperationPin'}
 }
 $newBytes=Exact $plan.NewScopePath $plan.NewScopeSha256;$scope=ConvertFrom-LAJson $newBytes
 if($scope.SourceApprovalSha256 -cne $plan.SourceApprovalSha256 -or $scope.SourceApprovalSha256 -ceq $oldScope.SourceApprovalSha256 -or $scope.ParentPolicySha256 -cne $oldScope.ParentPolicySha256 -or
  $scope.ProjectNumber -cne $oldScope.ProjectNumber -or $scope.CredentialReferenceId -cne $oldScope.CredentialReferenceId -or
  $scope.VoiceName -cne $oldScope.VoiceName -or $scope.LanguageCode -cne $oldScope.LanguageCode -or $scope.OutputBucket -cne $oldScope.OutputBucket -or
  $oldScope.AudioEncoding -cne 'LINEAR16' -or $scope.AudioEncoding -cne 'LINEAR16' -or $scope.TotalGoogleCapUsdCents -ne $oldScope.TotalGoogleCapUsdCents -or
  $scope.TaxFxProvisionUsdCents -ne $oldScope.TaxFxProvisionUsdCents -or $scope.UsdPerMillionCharacters -ne $oldScope.UsdPerMillionCharacters -or
  $scope.ApprovedRequestCeilingsUsdCents.Count -ne ($oldLedger.Claims.Count+1) -or $scope.DocumentJobs.Count -ne 1){throw 'LongAudioSuccessorScopeChange'}
 foreach($retained in $oldLedger.Claims){if(-not $scope.ApprovedRequestCeilingsUsdCents.Contains($retained.RequestBindingSha256) -or $scope.ApprovedRequestCeilingsUsdCents[$retained.RequestBindingSha256] -ne $retained.MaximumUsdCents){throw 'LongAudioSuccessorHistoryOmitted'}}
 $newJob=$scope.DocumentJobs[0];$oldJob=$oldScope.DocumentJobs[0]
 Assert-LARecord $plan.SourceExcerpt @('ParentDocumentId','ParentRevisionId','ParentSourceSha256','StartUtf16','EndUtf16Exclusive','ExcerptSha256')
 $excerpt=$plan.SourceExcerpt;Assert-LAHash $excerpt.ParentSourceSha256;Assert-LAHash $excerpt.ExcerptSha256;Assert-LAInteger $excerpt.StartUtf16;Assert-LAInteger $excerpt.EndUtf16Exclusive 1
 if(($plan.Version -eq 2 -and ([Guid]$excerpt.ParentDocumentId -ne [Guid]$oldJob.DocumentId -or [Guid]$excerpt.ParentRevisionId -ne [Guid]$oldJob.RevisionId -or
  $excerpt.ParentSourceSha256 -cne $oldJob.SourceSha256)) -or [Guid]$excerpt.ParentDocumentId -eq [Guid]::Empty -or [Guid]$excerpt.ParentRevisionId -eq [Guid]::Empty -or
  $newJob.SourceSha256 -cne $excerpt.ExcerptSha256 -or $newJob.InputMode -cne 'text' -or [Guid]$newJob.DocumentId -eq [Guid]$oldJob.DocumentId){throw 'LongAudioSuccessorExcerptIdentity'}
 # V3 binds the separately reviewed source revision directly, allowing a larger complete paragraph group after a successful shorter trial.
 $parentSource=Join-Path $profile ('documents/'+([Guid]$excerpt.ParentDocumentId).ToString('N')+'/content/'+([Guid]$excerpt.ParentRevisionId).ToString('N')+'.utf8')
 $parentText=[Text.UTF8Encoding]::new($false,$true).GetString((Exact $parentSource $excerpt.ParentSourceSha256))
 if($excerpt.EndUtf16Exclusive -gt $parentText.Length -or $excerpt.StartUtf16 -ge $excerpt.EndUtf16Exclusive -or [char]::IsLowSurrogate($parentText[$excerpt.StartUtf16]) -or
  ($excerpt.EndUtf16Exclusive -lt $parentText.Length -and [char]::IsLowSurrogate($parentText[$excerpt.EndUtf16Exclusive]))){throw 'LongAudioSuccessorExcerptBoundary'}
 if((Get-LAHash ([Text.Encoding]::UTF8.GetBytes($parentText.Substring($excerpt.StartUtf16,$excerpt.EndUtf16Exclusive-$excerpt.StartUtf16)))) -cne $excerpt.ExcerptSha256){throw 'LongAudioSuccessorExcerptChanged'}
 if($ids.Contains([Guid]$newJob.AttemptId) -or (Test-Path -LiteralPath (Join-Path $operations (([Guid]$newJob.AttemptId).ToString('N')+'.json')))){throw 'LongAudioSuccessorAttemptReplay'}
 $currentOnly=ConvertFrom-LAJson $newBytes;foreach($retained in $oldLedger.Claims){[void]$currentOnly.ApprovedRequestCeilingsUsdCents.Remove($retained.RequestBindingSha256)}
 Assert-LAScope $currentOnly $parent $scope.ParentPolicySha256
 $context=@{Scope=$currentOnly;Profile=$profile};Assert-LASavedInputs $context
 [decimal]$provision=[decimal]$parent.Budget.ProjectCeilingMinorUnits+[decimal]$scope.TaxFxProvisionUsdCents
 foreach($amount in $scope.ApprovedRequestCeilingsUsdCents.Values){$provision += [decimal]$amount}
 if($provision -gt $scope.TotalGoogleCapUsdCents){throw 'LongAudioSuccessorBudget'}
 $marker=ConvertFrom-LAJson (ConvertTo-LABytes $oldMarker);$marker.GoogleLongAudioScopeSha256=$plan.NewScopeSha256
 $ledger=[ordered]@{Version=1;ScopeSha256=$plan.NewScopeSha256;Claims=@($oldLedger.Claims)}
 return @{ScopeBytes=$newBytes;LedgerBytes=(ConvertTo-LABytes $ledger);MarkerBytes=(ConvertTo-LABytes $marker);OldRoot=$oldRoot;OldProvision=$oldProvision;Parent=$parent;Provision=$provision;Sealed=$sealed;RetainedCount=$oldLedger.Claims.Count}
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
if($ValidateOnly){[pscustomobject]@{Valid=$true;RetainedClaims=$context.RetainedCount;AdditionalApprovedClaims=1;TotalProvisionCents=$context.Provision;ProviderCalls=0};return}
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
   [pscustomobject]@{Updated=$true;RetainedClaims=$context.RetainedCount;NewCommitments=0;ProviderCalls=0;TotalProvisionCents=$context.Provision;History=$history}
  }finally{$budgetLease.Dispose()}
 }finally{$lease.Dispose()}
}finally{$instance.Dispose()}
