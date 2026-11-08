# A narrow offline-reviewed approval writer using the existing marker-last successor transaction.
# Its only permitted authority delta is one exact failed-child replacement, with prior replacement history retained.
function Inspect-LAReplacementTransition {
    $sealed=Test-Path -LiteralPath (Join-Path $history 'transition-plan.json')
    if($sealed){$null=Exact (Join-Path $history 'transition-plan.json') $ReviewedPlanSha256}
    $oldRoot=if($sealed){$history}else{$install}
    $oldScopeBytes=Exact (Join-Path $oldRoot 'google-long-audio-scope.json') $plan.PreviousScopeSha256
    $oldScope=ConvertFrom-LAJson $oldScopeBytes
    $oldLedgerPath=if($sealed){Join-Path $history 'budget-ledger.json'}else{Join-Path $profile 'data/google-long-audio/budget-ledger.json'}
    $oldLedger=ConvertFrom-LAJson (Exact $oldLedgerPath $plan.PreviousLedgerSha256)
    $oldMarker=ConvertFrom-LAJson (Exact (Join-Path $oldRoot 'cloudscribe-install.json') $plan.PreviousMarkerSha256)
    $oldProvision=if($sealed){Join-Path $history 'predecessor-provisioning'}else{Join-Path $install 'long-audio-provisioning'}
    if(-not (Test-Path -LiteralPath $oldProvision)){$oldProvision=Join-Path $history 'predecessor-provisioning-copy'}
    $null=Exact (Join-Path $oldProvision 'plan.json') $plan.PreviousProvisioningPlanSha256
    $receipt=ConvertFrom-LAJson (Exact (Join-Path $oldProvision 'receipt.json') $plan.PreviousProvisioningReceiptSha256)
    if($receipt.Phase -cne 'Complete' -or $receipt.PlanSha256 -cne $plan.PreviousProvisioningPlanSha256){throw 'LongAudioReplacementIncompletePredecessor'}
    $parentBytes=Exact (Join-Path $install 'chirp-article-admission-policy.json') $oldScope.ParentPolicySha256
    $parent=ConvertFrom-LAJson $parentBytes
    $parentJson=[Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false,$true).GetString($parentBytes),[Text.Json.JsonDocumentOptions]@{MaxDepth=64})
    try{Assert-ChirpPolicy $parentJson.RootElement}finally{$parentJson.Dispose()}
    if($oldMarker.Profile -cne $profile -or $oldMarker.ChirpArticlePolicySha256 -cne $oldScope.ParentPolicySha256 -or
        $parent.ProfileRootSha256 -cne (Get-LAHash ([Text.Encoding]::UTF8.GetBytes($profile.ToUpperInvariant()))) -or
        (Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.dll') 536870912)) -cne $parent.AppSha256 -or
        (Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.Infrastructure.dll') 536870912)) -cne $parent.InfrastructureSha256){throw 'LongAudioReplacementInstalledIdentity'}
    $data=Join-Path $profile 'data/google-long-audio'
    $oldContext=@{Root=$install;Profile=$profile;Marker=$oldMarker;MarkerPath=(Join-Path $oldRoot 'cloudscribe-install.json');Scope=$oldScope;
        ScopeBytes=$oldScopeBytes;ScopeSha=$plan.PreviousScopeSha256;ScopePath=(Join-Path $oldRoot 'google-long-audio-scope.json');
        ParentSha=$oldScope.ParentPolicySha256;ParentBytes=$parentBytes;Data=$data;Ledger=$oldLedgerPath;Transaction=$oldProvision;
        ProfileSha=$parent.ProfileRootSha256;AppSha=$parent.AppSha256;InfrastructureSha=$parent.InfrastructureSha256;MarkerBytes=(ConvertTo-LABytes $oldMarker)}
    Assert-LARetainedInstalledScope $oldContext $parent
    if($oldLedger.Claims.Count -eq 0 -or $plan.RetainedOperationPins -isnot [array] -or $plan.RetainedOperationPins.Count -ne $oldLedger.Claims.Count){throw 'LongAudioReplacementMissingHistory'}
    $operations=Join-Path $data 'operations';Assert-LAPhysical $operations
    $journals=@(Get-ChildItem -LiteralPath $operations -File -Filter '*.json')
    if($journals.Count -ne $oldLedger.Claims.Count){throw 'LongAudioReplacementUnexpectedOperations'}
    foreach($entry in @(Get-ChildItem -LiteralPath $operations -Force)){
        Assert-LAPhysical $entry.FullName
        if($entry.PSIsContainer -or ($entry.Extension -cne '.json' -and (-not $entry.Name.EndsWith('.lock',[StringComparison]::Ordinal) -or $entry.Length -ne 0))){throw 'LongAudioReplacementUnresolvedEvidence'}
    }
    $ids=[Collections.Generic.HashSet[Guid]]::new()
    foreach($pin in $plan.RetainedOperationPins){
        if($pin.Contains('CompletedReceiptSha256')){Assert-LARecord $pin @('AttemptId','Sha256','CompletedReceiptSha256','CompletedObjectSha256')}
        else{Assert-LARecord $pin @('AttemptId','Sha256')}
        Assert-LAHash $pin.Sha256
        $claims=@($oldLedger.Claims|Where-Object {[Guid]$_.AttemptId -eq [Guid]$pin.AttemptId})
        if($claims.Count -ne 1 -or -not $ids.Add([Guid]$pin.AttemptId)){throw 'LongAudioReplacementOperationIdentity'}
        $op=ConvertFrom-LAJson (Exact (Join-Path $operations (([Guid]$pin.AttemptId).ToString('N')+'.json')) $pin.Sha256)
        if([Guid]$op.AttemptId -ne [Guid]$pin.AttemptId -or $op.BindingSha256 -cne $claims[0].RequestBindingSha256 -or
            $op.State -notin @(2,3) -or $op.OperationParseFailed -or -not $op.Observation.ReadbackVerified -or $op.Observation.Truncated){throw 'LongAudioReplacementNotTerminal'}
        if([Guid]$pin.AttemptId -eq [Guid]$plan.PreviousAttemptId -and $pin.Sha256 -cne $plan.PreviousOperationSha256){throw 'LongAudioReplacementTargetPin'}
        if($op.State -eq 2){
            $stem=Join-Path $data ('output/'+([Guid]$pin.AttemptId).ToString('N'))
            $mediaPath=if(Test-Path -LiteralPath ($stem+'.recovery-verified.json')){$stem+'.recovery-verified.json'}else{$stem+'.verified.json'}
            if(-not $pin.Contains('CompletedReceiptSha256')){throw 'LongAudioReplacementCompletedPinsRequired'}
            Assert-LAHash $pin.CompletedReceiptSha256;Assert-LAHash $pin.CompletedObjectSha256
            $media=ConvertFrom-LAJson (Exact $mediaPath $pin.CompletedReceiptSha256);$artifact=$media.Artifact
            $object=ConvertFrom-LAJson (Exact ($stem+'.object.json') $pin.CompletedObjectSha256)
            $identity=$artifact.Identity
            foreach($key in @('BindingSha256','Bucket','ObjectName','Generation','Bytes','Md5Base64')){
                if($identity[$key] -cne $object[$key]){throw 'LongAudioReplacementObjectChanged'}
            }
            $bucketPrefix='gs://'+$oldScope.OutputBucket+'/'
            if(-not $op.OutputGcsUri.StartsWith($bucketPrefix,[StringComparison]::Ordinal)){throw 'LongAudioReplacementCompletedBucket'}
            Assert-LAPhysical ($stem+'.wav')
            if($media.Version -ne 1 -or $identity.BindingSha256 -cne $op.BindingSha256 -or $identity.Bucket -cne $oldScope.OutputBucket -or
                $identity.ObjectName -cne $op.OutputGcsUri.Substring($bucketPrefix.Length) -or $identity.Generation -cnotmatch '^[1-9][0-9]*$' -or
                $identity.Bytes -ne $artifact.Bytes -or $artifact.LocalPath -cne ($stem+'.wav') -or
                $artifact.Bytes -ne (Get-Item -LiteralPath ($stem+'.wav')).Length -or
                $artifact.AudioSha256 -cne (Get-FileHash -LiteralPath ($stem+'.wav') -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'LongAudioReplacementAcceptedAudioChanged'}
            if($claims[0].Contains('Recovery') -and $null -ne $claims[0].Recovery){
                if($media.RootBindingSha256 -cne $claims[0].Recovery.RootBindingSha256){throw 'LongAudioReplacementCompletedFamily'}
            }elseif($media.RequestBindingSha256 -cne $op.BindingSha256){throw 'LongAudioReplacementCompletedSource'}
            $audioStream=[IO.File]::OpenRead($stem+'.wav')
            try{$md5=[Convert]::ToBase64String([Security.Cryptography.MD5]::HashData($audioStream))}finally{$audioStream.Dispose()}
            if($md5 -cne $identity.Md5Base64){throw 'LongAudioReplacementCompletedChecksum'}
        }
    }
    $newBytes=Exact $plan.NewScopePath $plan.NewScopeSha256;$scope=ConvertFrom-LAJson $newBytes
    $null=Assert-LAReplacementScope $scope
    if($plan.Version -eq 6){Assert-LAFundingAdjustment $oldScope $scope $plan}
    else{
    $prior=@();if($oldScope.Contains('FailedRangeReplacements') -and $null -ne $oldScope.FailedRangeReplacements){$prior=@($oldScope.FailedRangeReplacements)}
    if($scope.SourceApprovalSha256 -cne $plan.SourceApprovalSha256 -or $scope.SourceApprovalSha256 -ceq $oldScope.SourceApprovalSha256 -or
        $scope.FailedRangeReplacements.Count -ne ($prior.Count+1)){throw 'LongAudioReplacementFreshApprovalRequired'}
    for($i=0;$i -lt $prior.Count;$i++){
        if((Get-LAHash (ConvertTo-LABytes $prior[$i])) -cne (Get-LAHash (ConvertTo-LABytes $scope.FailedRangeReplacements[$i]))){throw 'LongAudioReplacementHistoryChanged'}
    }
    $a=$scope.FailedRangeReplacements[-1]
    if((Get-LAHash (ConvertTo-LABytes $a)) -cne (Get-LAHash (ConvertTo-LABytes $plan.FailedRangeReplacement)) -or
        [Guid]$a.FailedAttemptId -ne [Guid]$plan.PreviousAttemptId -or $a.FailedCheckpointSha256 -cne $plan.PreviousOperationSha256 -or
        $ids.Contains([Guid]$a.ReplacementAttemptId) -or (Test-Path -LiteralPath (Join-Path $operations (([Guid]$a.ReplacementAttemptId).ToString('N')+'.json')))){throw 'LongAudioReplacementReplayOrChangedApproval'}
    $oldComparable=ConvertFrom-LAJson $oldScopeBytes;$newComparable=ConvertFrom-LAJson $newBytes
    foreach($value in @($oldComparable,$newComparable)){[void]$value.Remove('SourceApprovalSha256');[void]$value.Remove('FailedRangeReplacements')}
    if((Get-LAHash (ConvertTo-LABytes $oldComparable)) -cne (Get-LAHash (ConvertTo-LABytes $newComparable))){throw 'LongAudioReplacementUnrelatedScopeChange'}
    $target=@($oldLedger.Claims|Where-Object {[Guid]$_.AttemptId -eq [Guid]$a.FailedAttemptId})[0]
    $state=ConvertFrom-LAJson (Exact (Join-Path $operations (([Guid]$a.FailedAttemptId).ToString('N')+'.json')) $a.FailedCheckpointSha256)
    $proof=[ordered]@{RootBindingSha256=$a.RootBindingSha256;ParentBindingSha256=$a.FailedBindingSha256;StartScalar=$a.StartScalar;EndScalarExclusive=$a.EndScalarExclusive;PlannerVersion='long-audio-natural-recovery-v1';ParentCheckpointSha256=$a.FailedCheckpointSha256}
    $proposed=[ordered]@{AttemptId=$a.ReplacementAttemptId;RequestBindingSha256=$a.ReplacementBindingSha256;MaximumUsdCents=$a.MaximumUsdCents;Recovery=$proof}
    $context=@{}+$oldContext;$context.Scope=$scope
    Assert-LAExactReplacementClaim $context $proposed $target $proof $state (Read-LABytes (Join-Path $operations (([Guid]$a.FailedAttemptId).ToString('N')+'.json'))) @($a)
    }
    [decimal]$provision=[decimal]$parent.Budget.ProjectCeilingMinorUnits+[decimal]$scope.TaxFxProvisionUsdCents
    foreach($claim in $oldLedger.Claims){$provision += [decimal]$claim.MaximumUsdCents}
    foreach($entry in $scope.ApprovedRequestCeilingsUsdCents.GetEnumerator()){
        if(@($oldLedger.Claims|Where-Object {$_.RequestBindingSha256 -ceq $entry.Key}).Count -eq 0){$provision += [decimal]$entry.Value}
    }
    foreach($replacement in $scope.FailedRangeReplacements){
        if(@($oldLedger.Claims|Where-Object {[Guid]$_.AttemptId -eq [Guid]$replacement.ReplacementAttemptId}).Count -eq 0){$provision += [decimal]$replacement.MaximumUsdCents}
    }
    if($provision -gt $scope.TotalGoogleCapUsdCents){throw 'LongAudioReplacementBudget'}
    $marker=ConvertFrom-LAJson (ConvertTo-LABytes $oldMarker);$marker.GoogleLongAudioScopeSha256=$plan.NewScopeSha256
    $ledger=[ordered]@{Version=1;ScopeSha256=$plan.NewScopeSha256;Claims=@($oldLedger.Claims)}
    return @{ScopeBytes=$newBytes;LedgerBytes=(ConvertTo-LABytes $ledger);MarkerBytes=(ConvertTo-LABytes $marker);OldRoot=$oldRoot;OldProvision=$oldProvision;Parent=$parent;Provision=$provision;Sealed=$sealed;RetainedCount=$oldLedger.Claims.Count}
}
