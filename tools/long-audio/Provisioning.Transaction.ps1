function New-LAPlan($Context) {
    if($Context.Marker.Contains('GoogleLongAudioScopeSha256') -or (Test-Path -LiteralPath $Context.ScopePath) -or
        (Test-Path -LiteralPath $Context.Ledger) -or (Test-Path -LiteralPath $Context.Transaction)){throw 'LongAudioPriorProvisioningRequiresReconciliation'}
    if((Test-Path -LiteralPath $Context.Data) -and @(Get-ChildItem -LiteralPath $Context.Data -Force|Where-Object {$_.Name -cne 'budget-ledger.json.lock'}).Count -gt 0){throw 'LongAudioPriorEvidenceRequiresReconciliation'}
    if((Test-Path -LiteralPath ($Context.Ledger+'.lock')) -and (Get-Item -LiteralPath ($Context.Ledger+'.lock')).Length -ne 0){throw 'LongAudioUnexpectedLockContents'}
    $marker=ConvertFrom-LAJson $Context.MarkerBytes;$marker.GoogleLongAudioScopeSha256=$Context.ScopeSha
    $markerBytes=ConvertTo-LABytes $marker
    $ledgerBytes=ConvertTo-LABytes ([ordered]@{Version=1;ScopeSha256=$Context.ScopeSha;Claims=@()})
    $plan=[ordered]@{Version=1;TransactionId=[Guid]::NewGuid().ToString('D');ScopeSha256=$Context.ScopeSha;ParentPolicySha256=$Context.ParentSha;
        ProfileRootSha256=$Context.ProfileSha;AppSha256=$Context.AppSha;InfrastructureSha256=$Context.InfrastructureSha;
        PreviousMarkerSha256=(Get-LAHash $Context.MarkerBytes);NewMarkerSha256=(Get-LAHash $markerBytes);InitialLedgerSha256=(Get-LAHash $ledgerBytes);CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    return @{Plan=$plan;PlanBytes=(ConvertTo-LABytes $plan);NewMarkerBytes=$markerBytes;InitialLedgerBytes=$ledgerBytes}
}
function Read-LAPlan($Context) {
    $planBytes=Read-LABytes (Join-Path $Context.Transaction 'plan.json') 16384;$plan=ConvertFrom-LAJson $planBytes
    Assert-LARecord $plan @('Version','TransactionId','ScopeSha256','ParentPolicySha256','ProfileRootSha256','AppSha256','InfrastructureSha256',
        'PreviousMarkerSha256','NewMarkerSha256','InitialLedgerSha256','CreatedUtc')
    if($plan.Version -ne 1 -or [Guid]$plan.TransactionId -eq [Guid]::Empty -or $plan.ScopeSha256 -cne $Context.ScopeSha -or
        $plan.ParentPolicySha256 -cne $Context.ParentSha -or $plan.ProfileRootSha256 -cne $Context.ProfileSha -or
        $plan.AppSha256 -cne $Context.AppSha -or $plan.InfrastructureSha256 -cne $Context.InfrastructureSha){throw 'LongAudioTransactionBinding'}
    foreach($name in @('PreviousMarkerSha256','NewMarkerSha256','InitialLedgerSha256')){Assert-LAHash $plan[$name]}
    $new=Read-LABytes (Join-Path $Context.Transaction 'marker-after.json') 16384
    $old=Read-LABytes (Join-Path $Context.Transaction 'marker-before.json') 16384
    $ledger=Read-LABytes (Join-Path $Context.Transaction 'ledger-initial.json')
    if((Get-LAHash $new) -cne $plan.NewMarkerSha256 -or (Get-LAHash $old) -cne $plan.PreviousMarkerSha256 -or
        (Get-LAHash $ledger) -cne $plan.InitialLedgerSha256 -or (Get-LAHash (Read-LABytes (Join-Path $Context.Transaction 'scope.json'))) -cne $Context.ScopeSha){throw 'LongAudioSealedInputChanged'}
    $receiptPath=Join-Path $Context.Transaction 'receipt.json'
    if(Test-Path -LiteralPath $receiptPath){$receipt=ConvertFrom-LAJson (Read-LABytes $receiptPath 16384)}
    else{
        if((Get-LAHash $Context.MarkerBytes) -cne $plan.PreviousMarkerSha256 -or (Test-Path -LiteralPath $Context.ScopePath) -or (Test-Path -LiteralPath $Context.Ledger)){throw 'LongAudioMissingReceiptAfterEffects'}
        Assert-LAUnconsumed $Context $plan
        $receipt=@{Version=1;TransactionId=$plan.TransactionId;PlanSha256=(Get-LAHash $planBytes);Phase='Prepared'}
    }
    Assert-LARecord $receipt @('Version','TransactionId','PlanSha256','Phase')
    if($receipt.Version -ne 1 -or $receipt.TransactionId -cne $plan.TransactionId -or $receipt.PlanSha256 -cne (Get-LAHash $planBytes)){throw 'LongAudioReceiptBinding'}
    return @{Plan=$plan;PlanBytes=$planBytes;NewMarkerBytes=$new;PreviousMarkerBytes=$old;InitialLedgerBytes=$ledger;Receipt=$receipt}
}
function Set-LAPhase($Context,$Sealed,[string]$Phase,[scriptblock]$AfterCheckpoint) {
    $receipt=[ordered]@{Version=1;TransactionId=$Sealed.Plan.TransactionId;PlanSha256=(Get-LAHash $Sealed.PlanBytes);Phase=$Phase}
    $path=Join-Path $Context.Transaction 'receipt.json'
    Write-LAAtomic $path (ConvertTo-LABytes $receipt) -Replace:(Test-Path -LiteralPath $path)
    if($AfterCheckpoint){& $AfterCheckpoint $Phase}
}
function Assert-LACompleted($Context,$Sealed) {
    if((Get-LAHash (Read-LABytes $Context.MarkerPath 16384)) -cne $Sealed.Plan.NewMarkerSha256 -or
        (Get-LAHash (Read-LABytes $Context.ScopePath)) -cne $Context.ScopeSha){throw 'LongAudioInstalledReadback'}
    $ledger=ConvertFrom-LAJson (Read-LABytes $Context.Ledger)
    Assert-LARecord $ledger @('Version','ScopeSha256','Claims')
    if($ledger.Version -ne 1 -or $ledger.ScopeSha256 -cne $Context.ScopeSha -or $ledger.Claims -isnot [array]){throw 'LongAudioLedgerIdentity'}
    $ids=[Collections.Generic.HashSet[Guid]]::new();$keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $parentPolicy=ConvertFrom-LAJson $Context.ParentBytes
    [decimal]$provision=[decimal]$Context.Scope.TaxFxProvisionUsdCents+[decimal]$parentPolicy.Budget.ProjectCeilingMinorUnits
    foreach($claim in $ledger.Claims){
        $fields=@('AttemptId','RequestBindingSha256','MaximumUsdCents');if($claim.Contains('Recovery')){$fields += 'Recovery'}
        Assert-LARecord $claim $fields;Assert-LAInteger $claim.MaximumUsdCents 1;Assert-LAHash $claim.RequestBindingSha256
        if([Guid]$claim.AttemptId -eq [Guid]::Empty -or -not $ids.Add([Guid]$claim.AttemptId) -or -not $keys.Add([string]$claim.RequestBindingSha256) -or
            ($claim.Contains('Recovery') -and $null -ne $claim.Recovery -and $Context.Scope.ApprovedRequestCeilingsUsdCents.Contains([string]$claim.RequestBindingSha256))){throw 'LongAudioLedgerClaimChanged'}
        if($claim.Contains('Recovery') -and $null -ne $claim.Recovery){Assert-LARecoveryClaim $Context $ledger $claim}
        elseif(-not $Context.Scope.ApprovedRequestCeilingsUsdCents.Contains([string]$claim.RequestBindingSha256) -or
            $claim.MaximumUsdCents -ne $Context.Scope.ApprovedRequestCeilingsUsdCents[[string]$claim.RequestBindingSha256]){throw 'LongAudioLedgerClaimChanged'}
        $provision += [decimal]$claim.MaximumUsdCents
    }
    if($provision -gt [decimal]$Context.Scope.TotalGoogleCapUsdCents){throw 'LongAudioCumulativeBudget'}
    if($Context.Scope.Contains('DeferredUnknownRanges') -and $null -ne $Context.Scope.DeferredUnknownRanges){Assert-LADeferredEvidence $Context $ledger}
}

# Readback only: this validates retained native admission provenance and never grants a new request.
function Assert-LARecoveryClaim($Context,$Ledger,$Claim) {
    $proof=$Claim.Recovery
    Assert-LARecord $proof @('RootBindingSha256','ParentBindingSha256','StartScalar','EndScalarExclusive','PlannerVersion','ParentCheckpointSha256')
    foreach($key in @('RootBindingSha256','ParentBindingSha256','ParentCheckpointSha256')){Assert-LAHash $proof[$key]}
    Assert-LAInteger $proof.StartScalar;Assert-LAInteger $proof.EndScalarExclusive 1
    if($proof.EndScalarExclusive -le $proof.StartScalar -or $proof.PlannerVersion -cnotin @('long-audio-natural-recovery-v1','long-audio-explicit-unknown-regeneration-v1') -or
        -not $Context.Scope.Contains('AdaptiveRecoveryRootBindings') -or $proof.RootBindingSha256 -cnotin $Context.Scope.AdaptiveRecoveryRootBindings -or
        $Context.Scope.AudioEncoding -cne 'LINEAR16'){throw 'LongAudioRecoveryClaimShape'}
    $parents=@($Ledger.Claims|Where-Object {$_.RequestBindingSha256 -ceq $proof.ParentBindingSha256})
    if($parents.Count -ne 1 -or [Guid]$parents[0].AttemptId -eq [Guid]$Claim.AttemptId){throw 'LongAudioRecoveryParentClaim'}
    $parent=$parents[0];$journal=Join-Path $Context.Data ('operations/'+([Guid]$parent.AttemptId).ToString('N')+'.json')
    $bytes=Read-LABytes $journal 8192;$state=ConvertFrom-LAJson $bytes
    if($proof.PlannerVersion -ceq 'long-audio-explicit-unknown-regeneration-v1'){
        Assert-LAExplicitRegenerationClaim $Context $Ledger $Claim $parent $proof $state $bytes;return
    }
    $exact=@();if($Context.Scope.Contains('FailedRangeReplacements')){$exact=@($Context.Scope.FailedRangeReplacements|Where-Object {[Guid]$_.ReplacementAttemptId -eq [Guid]$Claim.AttemptId})}
    if($exact.Count -gt 0){Assert-LAExactReplacementClaim $Context $Claim $parent $proof $state $bytes $exact;return}
    if((Get-LAHash $bytes) -cne $proof.ParentCheckpointSha256 -or $state.BindingSha256 -cne $proof.ParentBindingSha256 -or
        $state.State -ne 3 -or -not $state.Contains('FailureKind') -or $state.FailureKind -ne 1 -or $state.OperationParseFailed -or
        -not $state.Observation.ReadbackVerified -or $state.Observation.ReadState -ne 1 -or $state.Observation.Truncated -or $state.Observation.Retention -ne 2 -or
        -not (($state.HttpStatus -eq 400 -and $null -eq $state.RpcCode -and $null -eq $state.OperationName) -or
            ($state.HttpStatus -eq 200 -and $state.RpcCode -eq 3 -and $state.OperationName -cmatch ('^projects/'+$Context.Scope.ProjectNumber+'/locations/global/operations/[A-Za-z0-9_-]+$')))){throw 'LongAudioRecoveryParentEvidence'}
    $output=Join-Path $Context.Data ('output/'+([Guid]$parent.AttemptId).ToString('N'))
    if((Test-Path -LiteralPath ($output+'.verified.json')) -or (Test-Path -LiteralPath ($output+'.recovery-verified.json'))){throw 'LongAudioRecoveryAcceptedParent'}
    $source=Get-LARecoveryRoot $Context $proof.RootBindingSha256
    $start=0;$end=$source.Offsets.Count-1
    if($parent.Contains('Recovery') -and $null -ne $parent.Recovery){
        if($parent.Recovery.RootBindingSha256 -cne $proof.RootBindingSha256){throw 'LongAudioRecoveryParentFamily'}
        $start=$parent.Recovery.StartScalar;$end=$parent.Recovery.EndScalarExclusive
    }elseif($parent.RequestBindingSha256 -cne $proof.RootBindingSha256){throw 'LongAudioRecoveryRootClaim'}
    if($start -lt 0 -or $end -gt ($source.Offsets.Count-1) -or $end -le $start){throw 'LongAudioRecoveryParentRange'}
    $parentText=$source.Text.Substring($source.Offsets[$start],$source.Offsets[$end]-$source.Offsets[$start])
    if((Get-LARecoveryBinding $Context.Scope ([Guid]$parent.AttemptId) $state.OutputGcsUri $parentText) -cne $proof.ParentBindingSha256){throw 'LongAudioRecoveryParentWire'}
    if($proof.StartScalar -lt $start -or $proof.EndScalarExclusive -gt $end -or
        ($proof.StartScalar -eq $start -and $proof.EndScalarExclusive -eq $end)){throw 'LongAudioRecoveryNoProgress'}
    $text=$source.Text.Substring($source.Offsets[$proof.StartScalar],$source.Offsets[$proof.EndScalarExclusive]-$source.Offsets[$proof.StartScalar])
    $identity=[Collections.Generic.Dictionary[string,object]]::new()
    $identity.Add('Version','long-audio-natural-recovery-v1');$identity.Add('BindingSha256',[string]$proof.ParentBindingSha256)
    $identity.Add('start',[int]$proof.StartScalar);$identity.Add('end',[int]$proof.EndScalarExclusive)
    $identity.Add('sourceSha',(Get-LAHash ([Text.Encoding]::UTF8.GetBytes($text))))
    $digest=[Security.Cryptography.SHA256]::HashData((ConvertTo-LAWire $identity));$id=[Guid]::new([byte[]]$digest[0..15])
    $uri=$state.OutputGcsUri.Substring(0,$state.OutputGcsUri.LastIndexOf('/')+1)+$id.ToString('N')+'.wav'
    $binding=Get-LARecoveryBinding $Context.Scope $id $uri $text
    $amount=[Math]::Max([decimal]1,[decimal]::Ceiling([decimal]$text.Length*[decimal]$Context.Scope.UsdPerMillionCharacters/10000))
    if($id -ne [Guid]$Claim.AttemptId -or $binding -cne $Claim.RequestBindingSha256 -or $amount -ne $Claim.MaximumUsdCents){throw 'LongAudioRecoveryExactChild'}
}

function Get-LARecoveryBinding($Scope,[Guid]$Attempt,[string]$Uri,[string]$Text) {
    $inputValue=[Collections.Generic.Dictionary[string,object]]::new();$inputValue.Add('text',$Text)
    $voice=[Collections.Generic.Dictionary[string,object]]::new();$voice.Add('languageCode',[string]$Scope.LanguageCode);$voice.Add('name',[string]$Scope.VoiceName)
    $audio=[Collections.Generic.Dictionary[string,object]]::new();$audio.Add('audioEncoding',[string]$Scope.AudioEncoding)
    $wire=[Collections.Generic.Dictionary[string,object]]::new();$wire.Add('input',$inputValue);$wire.Add('voice',$voice);$wire.Add('audioConfig',$audio);$wire.Add('outputGcsUri',$Uri)
    $binding=[Collections.Generic.Dictionary[string,object]]::new();$binding.Add('attemptId',$Attempt);$binding.Add('projectNumber',[string]$Scope.ProjectNumber)
    $binding.Add('location','global');$binding.Add('credentialReferenceId',[string]$Scope.CredentialReferenceId);$binding.Add('outputGcsUri',$Uri)
    $binding.Add('PayloadSha256',(Get-LAHash (ConvertTo-LAWire $wire)))
    return Get-LAHash (ConvertTo-LAWire $binding)
}

function Get-LARecoveryRoot($Context,[string]$Binding) {
    if(-not $Context.Contains('RecoveryRootCache')){$Context.RecoveryRootCache=@{}}
    if($Context.RecoveryRootCache.Contains($Binding)){return $Context.RecoveryRootCache[$Binding]}
    foreach($job in $Context.Scope.DocumentJobs){
        $path=Join-Path $Context.Profile ('documents/'+([Guid]$job.DocumentId).ToString('N')+'/content/'+([Guid]$job.RevisionId).ToString('N')+'.utf8')
        $bytes=Read-LABytes $path 1000000;if((Get-LAHash $bytes) -cne $job.SourceSha256){throw 'LongAudioRecoverySourceChanged'}
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        if((Get-LARecoveryBinding $Context.Scope ([Guid]$job.AttemptId) $job.OutputGcsUri $text) -cne $Binding){continue}
        $offsets=[Collections.Generic.List[int]]::new();$offsets.Add(0)
        for($i=0;$i -lt $text.Length;){$i += $(if([char]::IsHighSurrogate($text[$i])){2}else{1});$offsets.Add($i)}
        $value=@{Text=$text;Offsets=$offsets};$Context.RecoveryRootCache[$Binding]=$value;return $value
    }
    throw 'LongAudioRecoveryRootSourceMissing'
}
function Invoke-LAProvisioning([string]$InstallRoot,[string]$ProfileRoot,[string]$ScopePath,[string]$ScopeSha256,
    [ValidateSet('Inspect','Install','Resume','Rollback')][string]$Phase='Inspect',[scriptblock]$AfterCheckpoint) {
    $context=Get-LAContext $InstallRoot $ProfileRoot $ScopePath $ScopeSha256
    Assert-LASavedInputs $context
    $existing=Test-Path -LiteralPath $context.Transaction
    $sealed=if($existing){Read-LAPlan $context}else{New-LAPlan $context}
    if($existing -and $sealed.Receipt.Phase -ceq 'Complete' -and $Phase -cne 'Rollback'){
        Assert-LACompleted $context $sealed
        return [pscustomobject]@{Verified=$true;AlreadyComplete=$true;NewCommitments=0;ProviderCalls=0}
    }
    if($Phase -ceq 'Inspect'){
        if($existing -and $sealed.Receipt.Phase -cin @('RollingBack','RolledBack')){throw 'LongAudioRollbackMustBeCompletedOrReviewed'}
        Assert-LAUnconsumed $context $sealed.Plan
        return [pscustomobject]@{Valid=$true;Pending=$existing;NewCommitments=0;ProviderCalls=0}
    }
    Assert-LAClosed $context.Root
    $instancePath=Join-Path $context.Profile '.instance.lock';Assert-LAPhysical $instancePath
    $instanceLease=[IO.FileStream]::new($instancePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
    $leasePath=Join-Path $context.Root 'long-audio-provisioning.lock';Assert-LAPhysical $leasePath
    $installLease=[IO.FileStream]::new($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $context=Get-LAContext $InstallRoot $ProfileRoot $ScopePath $ScopeSha256;Assert-LASavedInputs $context
        [IO.Directory]::CreateDirectory($context.Data)|Out-Null
        $ledgerLock=$context.Ledger+'.lock';Assert-LAPhysical $ledgerLock
        $financialLease=[IO.FileStream]::new($ledgerLock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try{ Invoke-LAProvisioningLocked $context $Phase $AfterCheckpoint }
        finally{$financialLease.Dispose()}
    }finally{$installLease.Dispose()}
    }finally{$instanceLease.Dispose()}
}
function Invoke-LAProvisioningLocked($Context,[string]$Phase,[scriptblock]$AfterCheckpoint) {
    $exists=Test-Path -LiteralPath $Context.Transaction
    if($exists){
        $sealed=Read-LAPlan $Context
        if($sealed.Receipt.Phase -ceq 'Complete' -and $Phase -cne 'Rollback') {Assert-LACompleted $Context $sealed;return [pscustomobject]@{Verified=$true;AlreadyComplete=$true;NewCommitments=0;ProviderCalls=0}}
        if($Phase -ceq 'Install'){throw 'LongAudioUseSamePlanResume'}
    }else{
        if($Phase -ceq 'Rollback'){
            $null=New-LAPlan $Context
            return [pscustomobject]@{RolledBack=$true;NoActiveAuthority=$true;UnpublishedStagingPreserved=$true;NewCommitments=0;ProviderCalls=0}
        }
        $sealed=New-LAPlan $Context
        $stage=$Context.Transaction+'.stage-'+[Guid]::NewGuid().ToString('N');Assert-LAPhysical $stage
        if([IO.Path]::GetDirectoryName($stage) -cne $Context.Root){throw 'LongAudioStagingRoot'}
        [IO.Directory]::CreateDirectory($stage)|Out-Null
        foreach($entry in @(@('scope.json',$Context.ScopeBytes),@('marker-before.json',$Context.MarkerBytes),@('marker-after.json',$sealed.NewMarkerBytes),@('ledger-initial.json',$sealed.InitialLedgerBytes),@('plan.json',$sealed.PlanBytes))){
            Set-LAExactFile (Join-Path $stage $entry[0]) $entry[1]
            if($AfterCheckpoint){& $AfterCheckpoint ('Staged-'+$entry[0])}
        }
        $prepared=[ordered]@{Version=1;TransactionId=$sealed.Plan.TransactionId;PlanSha256=(Get-LAHash $sealed.PlanBytes);Phase='Prepared'}
        Set-LAExactFile (Join-Path $stage 'receipt.json') (ConvertTo-LABytes $prepared)
        if($AfterCheckpoint){& $AfterCheckpoint 'Staged-receipt.json'}
        [IO.Directory]::Move($stage,$Context.Transaction) # Atomic publication on this same parent/volume; abandoned staging never activates.
        Set-LAPhase $Context $sealed 'Prepared' $AfterCheckpoint
    }
    Assert-LAUnconsumed $Context $sealed.Plan
    $markerHash=Get-LAHash (Read-LABytes $Context.MarkerPath 16384)
    if($markerHash -cnotin @($sealed.Plan.PreviousMarkerSha256,$sealed.Plan.NewMarkerSha256)){throw 'LongAudioMarkerChanged'}
    if($Phase -ceq 'Rollback'){
        Set-LAPhase $Context $sealed 'RollingBack' $AfterCheckpoint
        Write-LAAtomic $Context.MarkerPath $sealed.PreviousMarkerBytes -Replace
        foreach($entry in @(@($Context.ScopePath,$Context.ScopeSha),@($Context.Ledger,$sealed.Plan.InitialLedgerSha256))){
            if(Test-Path -LiteralPath $entry[0]){if((Get-LAHash (Read-LABytes $entry[0])) -cne $entry[1]){throw 'LongAudioRollbackEvidenceChanged'};Remove-Item -LiteralPath $entry[0]}
        }
        Set-LAPhase $Context $sealed 'RolledBack' $AfterCheckpoint
        return [pscustomobject]@{RolledBack=$true;HistoryPreserved=$true;NewCommitments=0;ProviderCalls=0}
    }
    if($exists -and $sealed.Receipt.Phase -cin @('RollingBack','RolledBack')){throw 'LongAudioRollbackCannotReactivate'}
    Set-LAExactFile $Context.ScopePath $Context.ScopeBytes;Set-LAPhase $Context $sealed 'ScopeWritten' $AfterCheckpoint
    Set-LAExactFile $Context.Ledger $sealed.InitialLedgerBytes;Set-LAPhase $Context $sealed 'LedgerWritten' $AfterCheckpoint
    Write-LAAtomic $Context.MarkerPath $sealed.NewMarkerBytes -Replace;Set-LAPhase $Context $sealed 'MarkerPublished' $AfterCheckpoint
    Assert-LACompleted $Context $sealed
    Set-LAPhase $Context $sealed 'Complete' $AfterCheckpoint
    [pscustomobject]@{Installed=$true;Verified=$true;NewCommitments=0;ProviderCalls=0}
}
