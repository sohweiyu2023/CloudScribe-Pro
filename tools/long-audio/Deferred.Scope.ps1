# Explicit migration for the one owner of the legacy singleton observation guard. Read-only evidence validation.
function Assert-LADeferredScope($Scope) {
    if(-not $Scope.Contains('DeferredUnknownRanges') -or $null -eq $Scope.DeferredUnknownRanges){return}
    if($Scope.DeferredUnknownRanges -isnot [array] -or $Scope.DeferredUnknownRanges.Count -ne 1){throw 'LongAudioDeferredLegacyOwnerShape'}
    $d=$Scope.DeferredUnknownRanges[0]
    $fields=@('AttemptId','RootBindingSha256','StartScalar','EndScalarExclusive','CheckpointSha256','HeldClaimSha256','LegacyGuardSha256')
    if($d.Contains('ExplicitRegeneration')){$fields+='ExplicitRegeneration'}
    Assert-LARecord $d $fields
    foreach($key in @('RootBindingSha256','CheckpointSha256','HeldClaimSha256','LegacyGuardSha256')){Assert-LAHash $d[$key]}
    Assert-LAInteger $d.StartScalar;Assert-LAInteger $d.EndScalarExclusive 1
    if([Guid]$d.AttemptId -eq [Guid]::Empty -or $d.EndScalarExclusive -le $d.StartScalar -or
        $d.RootBindingSha256 -cnotin $Scope.AdaptiveRecoveryRootBindings){throw 'LongAudioDeferredSourceShape'}
    if($d.Contains('ExplicitRegeneration') -and $null -ne $d.ExplicitRegeneration){
        $r=$d.ExplicitRegeneration;Assert-LARecord $r @('AttemptId','BindingSha256','OutputGcsUri','MaximumUsdCents')
        Assert-LAHash $r.BindingSha256;Assert-LAInteger $r.MaximumUsdCents 1
        if([Guid]$r.AttemptId -eq [Guid]::Empty -or [Guid]$r.AttemptId -eq [Guid]$d.AttemptId -or
            $Scope.ApprovedRequestCeilingsUsdCents.Contains([string]$r.BindingSha256) -or
            @($Scope.DocumentJobs|Where-Object {[Guid]$_.AttemptId -eq [Guid]$r.AttemptId}).Count -gt 0 -or
            -not $r.OutputGcsUri.StartsWith(('gs://'+$Scope.OutputBucket+'/'),[StringComparison]::Ordinal)){
            throw 'LongAudioExplicitRegenerationShape'
        }
        if($Scope.Contains('FailedRangeReplacements') -and @($Scope.FailedRangeReplacements|Where-Object {
            [Guid]$_.ReplacementAttemptId -eq [Guid]$r.AttemptId -or [Guid]$_.FailedAttemptId -eq [Guid]$r.AttemptId}).Count -gt 0){throw 'LongAudioExplicitRegenerationIdentityReuse'}
    }
}
function Assert-LADeferredEvidence($Context,$Ledger) {
    Assert-LADeferredScope $Context.Scope
    if(-not $Context.Scope.Contains('DeferredUnknownRanges') -or $null -eq $Context.Scope.DeferredUnknownRanges){throw 'LongAudioDeferredApprovalMissing'}
    foreach($d in $Context.Scope.DeferredUnknownRanges){
        $id=[Guid]$d.AttemptId;$stem=$id.ToString('N')
        $path=Join-Path $Context.Data ('operations/'+$stem+'.json');$bytes=Read-LABytes $path 8192;$state=ConvertFrom-LAJson $bytes
        $claims=@($Ledger.Claims|Where-Object {[Guid]$_.AttemptId -eq $id})
        if($claims.Count -ne 1 -or (Get-LAHash $bytes) -cne $d.CheckpointSha256 -or
            $state.State -ne 0 -or $null -ne $state.OperationName -or $state.OperationParseFailed -or
            $state.HttpStatus -ne 0 -or $null -ne $state.RpcCode -or -not $state.Observation.ReadbackVerified -or
            $state.Observation.Retention -ne 2 -or $state.Observation.ReadState -eq 1 -or
            [Guid]$state.AttemptId -ne $id -or $state.BindingSha256 -cne $claims[0].RequestBindingSha256 -or
            (Get-LAHash (ConvertTo-LABytes $claims[0])) -cne $d.HeldClaimSha256){throw 'LongAudioDeferredEvidenceChanged'}
        $claim=$claims[0]
        if(-not $claim.Contains('Recovery') -or $null -eq $claim.Recovery -or
            $claim.Recovery.RootBindingSha256 -cne $d.RootBindingSha256 -or $claim.Recovery.StartScalar -ne $d.StartScalar -or
            $claim.Recovery.EndScalarExclusive -ne $d.EndScalarExclusive){throw 'LongAudioDeferredRangeChanged'}
        Assert-LARecoveryClaim $Context $Ledger $claim
        $source=Get-LARecoveryRoot $Context $d.RootBindingSha256
        if($d.EndScalarExclusive -ge $source.Offsets.Count){throw 'LongAudioDeferredSourceBounds'}
        $text=$source.Text.Substring($source.Offsets[$d.StartScalar],$source.Offsets[$d.EndScalarExclusive]-$source.Offsets[$d.StartScalar])
        if((Get-LARecoveryBinding $Context.Scope $id $state.OutputGcsUri $text) -cne $state.BindingSha256){throw 'LongAudioDeferredWireChanged'}
        if($d.Contains('ExplicitRegeneration') -and $null -ne $d.ExplicitRegeneration){
            $r=$d.ExplicitRegeneration;$newId=[Guid]$r.AttemptId
            $uri=$state.OutputGcsUri.Substring(0,$state.OutputGcsUri.LastIndexOf('/')+1)+$newId.ToString('N')+'.wav'
            $amount=[Math]::Max([decimal]1,[decimal]::Ceiling([decimal]$text.Length*[decimal]$Context.Scope.UsdPerMillionCharacters/10000))
            if($r.OutputGcsUri -cne $uri -or $r.BindingSha256 -ceq $state.BindingSha256 -or
                (Get-LARecoveryBinding $Context.Scope $newId $uri $text) -cne $r.BindingSha256 -or $amount -gt $r.MaximumUsdCents){throw 'LongAudioExplicitRegenerationWireOrPrice'}
        }
        $guard=Read-LABytes (Join-Path $Context.Data 'operations/observation-pending.guard') 32
        if($guard.Length -ne 32 -or (Get-LAHash $guard) -cne $d.LegacyGuardSha256 -or
            [Text.Encoding]::ASCII.GetString($guard) -cne $stem){throw 'LongAudioDeferredGuardChanged'}
        foreach($suffix in @('.wav','.object.json','.verified.json','.recovery-verified.json')){
            $output=Join-Path $Context.Data ('output/'+$stem+$suffix);Assert-LAPhysical $output
            if(Test-Path -LiteralPath $output){throw 'LongAudioDeferredAcceptedOrOrphanedOutput'}
        }
    }
}

function Assert-LAExplicitRegenerationClaim($Context,$Ledger,$Claim,$Parent,$Proof,$State,$Bytes) {
    Assert-LADeferredEvidence $Context $Ledger
    $matches=@($Context.Scope.DeferredUnknownRanges|Where-Object {$_.Contains('ExplicitRegeneration') -and $null -ne $_.ExplicitRegeneration -and [Guid]$_.ExplicitRegeneration.AttemptId -eq [Guid]$Claim.AttemptId})
    if($matches.Count -ne 1){throw 'LongAudioExplicitRegenerationApprovalMissing'}
    $d=$matches[0];$r=$d.ExplicitRegeneration
    if([Guid]$Parent.AttemptId -ne [Guid]$d.AttemptId -or $Proof.RootBindingSha256 -cne $d.RootBindingSha256 -or
        $Proof.StartScalar -ne $d.StartScalar -or $Proof.EndScalarExclusive -ne $d.EndScalarExclusive -or
        $Proof.ParentCheckpointSha256 -cne $d.CheckpointSha256 -or (Get-LAHash $Bytes) -cne $d.CheckpointSha256 -or
        $State.State -ne 0 -or $null -ne $State.OperationName -or $State.BindingSha256 -cne $Proof.ParentBindingSha256 -or
        $Claim.RequestBindingSha256 -cne $r.BindingSha256 -or $Claim.MaximumUsdCents -ne $r.MaximumUsdCents){throw 'LongAudioExplicitRegenerationClaimChanged'}
}

function Assert-LAExplicitRegenerationTransition($OldScope,$NewScope,$Ledger,[string]$Profile) {
    if($OldScope.DeferredUnknownRanges){
        if($OldScope.DeferredUnknownRanges.Count -ne $NewScope.DeferredUnknownRanges.Count){throw 'LongAudioDeferredOriginalMustRemain'}
        for($i=0;$i -lt $OldScope.DeferredUnknownRanges.Count;$i++){
            $previous=ConvertFrom-LAJson (ConvertTo-LABytes $OldScope.DeferredUnknownRanges[$i]);$next=ConvertFrom-LAJson (ConvertTo-LABytes $NewScope.DeferredUnknownRanges[$i])
            if(-not $previous.Contains('ExplicitRegeneration')){$previous.ExplicitRegeneration=$null}
            if(-not $next.Contains('ExplicitRegeneration')){$next.ExplicitRegeneration=$null}
            Assert-LABuildOnlyFields (ConvertTo-LABytes $previous) (ConvertTo-LABytes $next) @('ExplicitRegeneration')
            if($null -ne $previous.ExplicitRegeneration -and
                (ConvertTo-Json -InputObject $previous.ExplicitRegeneration -Depth 64 -Compress) -cne (ConvertTo-Json -InputObject $next.ExplicitRegeneration -Depth 64 -Compress)){
                throw 'LongAudioExplicitRegenerationCannotBeReplacedOrCleared'
            }
        }
    }
    foreach($descriptor in $NewScope.DeferredUnknownRanges){
        if(-not $descriptor.Contains('ExplicitRegeneration') -or $null -eq $descriptor.ExplicitRegeneration){continue}
        $previous=@($OldScope.DeferredUnknownRanges|Where-Object {[Guid]$_.AttemptId -eq [Guid]$descriptor.AttemptId})
        if($previous.Count -eq 1 -and $previous[0].Contains('ExplicitRegeneration') -and $null -ne $previous[0].ExplicitRegeneration){continue}
        $r=$descriptor.ExplicitRegeneration;$id=[Guid]$r.AttemptId;$stem=$id.ToString('N')
        if(@($Ledger.Claims|Where-Object {[Guid]$_.AttemptId -eq $id -or $_.RequestBindingSha256 -ceq $r.BindingSha256}).Count -gt 0){throw 'LongAudioExplicitRegenerationAlreadyClaimed'}
        foreach($folder in @('operations','output')){
            $directory=Join-Path $Profile ('data/google-long-audio/'+$folder);Assert-LAPhysical $directory
            if(@(Get-ChildItem -LiteralPath $directory -Force -Filter ($stem+'*')).Count -gt 0){throw 'LongAudioExplicitRegenerationEvidenceAlreadyExists'}
        }
    }
}
