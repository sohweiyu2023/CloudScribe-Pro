# Read-only parity with the native exact failed-child replacement admission. This file never writes authority or submits.
function Assert-LAReplacementScope($Scope) {
    [decimal]$total=0
    if(-not $Scope.Contains('FailedRangeReplacements') -or $null -eq $Scope.FailedRangeReplacements){return $total}
    if($Scope.FailedRangeReplacements -isnot [array]){throw 'LongAudioReplacementShape'}
    $old=[Collections.Generic.HashSet[Guid]]::new();$fresh=[Collections.Generic.HashSet[Guid]]::new()
    foreach($item in $Scope.FailedRangeReplacements){
        Assert-LARecord $item @('RootBindingSha256','FailedAttemptId','FailedBindingSha256','FailedCheckpointSha256','StartScalar','EndScalarExclusive','SourceSha256','ReplacementAttemptId','ReplacementOutputGcsUri','ReplacementBindingSha256','MaximumUsdCents')
        foreach($key in @('RootBindingSha256','FailedBindingSha256','FailedCheckpointSha256','SourceSha256','ReplacementBindingSha256')){Assert-LAHash $item[$key]}
        Assert-LAInteger $item.StartScalar;Assert-LAInteger $item.EndScalarExclusive 1;Assert-LAInteger $item.MaximumUsdCents 1
        if([Guid]$item.FailedAttemptId -eq [Guid]::Empty -or [Guid]$item.ReplacementAttemptId -eq [Guid]::Empty -or
            -not $old.Add([Guid]$item.FailedAttemptId) -or -not $fresh.Add([Guid]$item.ReplacementAttemptId) -or
            $item.EndScalarExclusive -le $item.StartScalar -or $item.RootBindingSha256 -cnotin $Scope.AdaptiveRecoveryRootBindings -or
            -not $Scope.ApprovedRequestCeilingsUsdCents.Contains($item.RootBindingSha256) -or
            $Scope.ApprovedRequestCeilingsUsdCents.Contains($item.ReplacementBindingSha256) -or
            $item.ReplacementBindingSha256 -ceq $item.FailedBindingSha256 -or
            @($Scope.DocumentJobs|Where-Object {[Guid]$_.AttemptId -eq [Guid]$item.ReplacementAttemptId}).Count -ne 0 -or
            -not $item.ReplacementOutputGcsUri.StartsWith(('gs://'+$Scope.OutputBucket+'/'),[StringComparison]::Ordinal)){throw 'LongAudioReplacementIdentity'}
        $total += [decimal]$item.MaximumUsdCents
    }
    if($old.Overlaps($fresh)){throw 'LongAudioReplacementChain'}
    foreach($group in @($Scope.FailedRangeReplacements|Group-Object RootBindingSha256)){
        $end=-1;foreach($item in @($group.Group|Sort-Object StartScalar)){
            if($item.StartScalar -lt $end){throw 'LongAudioReplacementOverlap'};$end=$item.EndScalarExclusive
        }
    }
    return $total
}
function Assert-LAExactReplacementClaim($Context,$Claim,$Parent,$Proof,$State,[byte[]]$Bytes,$Approvals) {
    $null=Assert-LAReplacementScope $Context.Scope
    if($Approvals.Count -ne 1){throw 'LongAudioReplacementAmbiguous'};$a=$Approvals[0]
    if(-not $Parent.Contains('Recovery') -or $null -eq $Parent.Recovery -or
        $a.RootBindingSha256 -cne $Proof.RootBindingSha256 -or $a.FailedBindingSha256 -cne $Proof.ParentBindingSha256 -or
        [Guid]$a.FailedAttemptId -ne [Guid]$Parent.AttemptId -or [Guid]$a.FailedAttemptId -ne [Guid]$State.AttemptId -or
        $a.FailedCheckpointSha256 -cne (Get-LAHash $Bytes) -or $a.FailedCheckpointSha256 -cne $Proof.ParentCheckpointSha256 -or
        $State.BindingSha256 -cne $a.FailedBindingSha256 -or $State.State -ne 3 -or $State.HttpStatus -ne 200 -or $State.RpcCode -ne 13 -or
        $State.FailureKind -ne 5 -or $State.OperationParseFailed -or -not $State.Observation.ReadbackVerified -or
        $State.Observation.ReadState -ne 1 -or $State.Observation.Truncated -or $State.Observation.Retention -ne 2 -or
        $State.OperationName -cnotmatch ('^projects/'+$Context.Scope.ProjectNumber+'/locations/global/operations/[A-Za-z0-9_-]+$') -or
        $Proof.StartScalar -ne $a.StartScalar -or $Proof.EndScalarExclusive -ne $a.EndScalarExclusive -or
        $Parent.Recovery.StartScalar -ne $a.StartScalar -or $Parent.Recovery.EndScalarExclusive -ne $a.EndScalarExclusive -or
        $Parent.Recovery.RootBindingSha256 -cne $a.RootBindingSha256 -or
        $Claim.RequestBindingSha256 -cne $a.ReplacementBindingSha256 -or $Claim.MaximumUsdCents -ne $a.MaximumUsdCents){throw 'LongAudioReplacementEvidence'}
    $stem=Join-Path $Context.Data ('output/'+([Guid]$Parent.AttemptId).ToString('N'))
    foreach($suffix in @('.wav','.object.json','.verified.json','.recovery-verified.json')){
        Assert-LAPhysical ($stem+$suffix);if(Test-Path -LiteralPath ($stem+$suffix)){throw 'LongAudioReplacementAcceptedOrOrphanedOutput'}
    }
    $source=Get-LARecoveryRoot $Context $a.RootBindingSha256
    if($a.StartScalar -lt 0 -or $a.EndScalarExclusive -ge $source.Offsets.Count){throw 'LongAudioReplacementRange'}
    $text=$source.Text.Substring($source.Offsets[$a.StartScalar],$source.Offsets[$a.EndScalarExclusive]-$source.Offsets[$a.StartScalar])
    $uri=$State.OutputGcsUri.Substring(0,$State.OutputGcsUri.LastIndexOf('/')+1)+([Guid]$a.ReplacementAttemptId).ToString('N')+'.wav'
    $amount=[Math]::Max([decimal]1,[decimal]::Ceiling([decimal]$text.Length*[decimal]$Context.Scope.UsdPerMillionCharacters/10000))
    if((Get-LAHash ([Text.Encoding]::UTF8.GetBytes($text))) -cne $a.SourceSha256 -or $uri -cne $a.ReplacementOutputGcsUri -or
        (Get-LARecoveryBinding $Context.Scope ([Guid]$Parent.AttemptId) $State.OutputGcsUri $text) -cne $a.FailedBindingSha256 -or
        (Get-LARecoveryBinding $Context.Scope ([Guid]$Claim.AttemptId) $uri $text) -cne $a.ReplacementBindingSha256 -or
        $amount -gt $a.MaximumUsdCents){throw 'LongAudioReplacementWireOrCost'}
}
