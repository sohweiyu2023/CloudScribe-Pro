# Build-only rebind. No request/job/budget expansion is accepted by this path.
function Get-LABuildInventory([string]$Root) {
    Assert-LAPhysical $Root
    $rows=@(foreach($entry in @(Get-ChildItem -LiteralPath $Root -Force -Recurse)){
        Assert-LAPhysical $entry.FullName
        if($entry.PSIsContainer){continue}
        $relative=[IO.Path]::GetRelativePath($Root,$entry.FullName).Replace('\','/')
        if($entry.Name.EndsWith('.lock',[StringComparison]::Ordinal)){
            if($entry.Length -ne 0){throw 'LongAudioBuildNonemptyLock'};continue
        }
        [ordered]@{Path=$relative;Bytes=[long]$entry.Length;Sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    return ,@($rows|Sort-Object -Property Path -CaseSensitive)
}
function Get-LABuildTreeHash([string]$Root) {return Get-LAHash (ConvertTo-LABytes (Get-LABuildInventory $Root))}
function Read-LABuildPin([string]$Path,[string]$Sha) {
    Assert-LAHash $Sha;$bytes=Read-LABytes $Path
    if((Get-LAHash $bytes) -cne $Sha){throw 'LongAudioBuildInputChanged'};return ,$bytes
}
function Assert-LABuildOnlyFields([byte[]]$Old,[byte[]]$New,[string[]]$Allowed) {
    $left=ConvertFrom-LAJson $Old;$right=ConvertFrom-LAJson $New
    if($left.Count -ne $right.Count){throw 'LongAudioBuildRecordShapeChanged'}
    foreach($key in $left.Keys){
        if(-not $right.Contains($key)){throw 'LongAudioBuildRecordShapeChanged'}
        if($key -cin $Allowed){continue}
        if((ConvertTo-Json -InputObject $left[$key] -Depth 64 -Compress) -cne (ConvertTo-Json -InputObject $right[$key] -Depth 64 -Compress)){throw 'LongAudioBuildAuthorityChanged'}
    }
}
function Get-LABuildRebind([string]$Install,[string]$Profile,[string]$Package,[string]$PlanPath,[string]$PlanSha) {
    $planBytes=Read-LABuildPin $PlanPath $PlanSha;$plan=ConvertFrom-LAJson $planBytes
    Assert-LAInteger $plan.Version 1
    $planFields=@('Version','TransactionId','SourceApprovalSha256','PreviousMarkerSha256','PreviousParentSha256','PreviousScopeSha256',
        'PreviousLedgerSha256','PreviousAppTreeSha256','PreviousProvisioningTreeSha256','ProfileTreeSha256','PackageAppTreeSha256','NewParentPath','NewParentSha256','NewScopePath','NewScopeSha256',
        'NativeProofPath','NativeProofSha256')
    if($plan.Version -ge 2){$planFields+='DeferredEvidenceSha256'}
    if($plan.Version -eq 3){$planFields+=@('AdditiveRegistrySha256','AdditiveSidecarTreeSha256','PreviousLegacyPolicyIdentitySha256','NewLegacyPolicyIdentitySha256','SourceApprovalPath')}
    Assert-LARecord $plan $planFields
    if($plan.Version -notin @(1,2,3) -or [Guid]$plan.TransactionId -eq [Guid]::Empty){throw 'LongAudioBuildPlanShape'}
    foreach($key in $plan.Keys|Where-Object {$_ -like '*Sha256'}){Assert-LAHash $plan[$key]}
    foreach($path in @($Install,$Profile,$Package,$plan.NewParentPath,$plan.NewScopePath,$plan.NativeProofPath)){Assert-LAPhysical $path}
    $install=[IO.Path]::GetFullPath($Install).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $profile=[IO.Path]::GetFullPath($Profile).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if($profile.StartsWith($install+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        $install.StartsWith($profile+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $profile -ieq $install){throw 'LongAudioBuildRootOverlap'}
    $inputPaths=@($Package,$PlanPath,$plan.NewParentPath,$plan.NewScopePath,$plan.NativeProofPath)
    if($plan.Version -eq 3){$inputPaths+=$plan.SourceApprovalPath}
    foreach($inputPath in $inputPaths){
        Assert-LAPhysical $inputPath
        $full=[IO.Path]::GetFullPath($inputPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
        foreach($protected in @($install,$profile)){
            if($full -ieq $protected -or $full.StartsWith($protected+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
                $protected.StartsWith($full+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'LongAudioBuildInputOverlap'}
        }
    }
    $history=Join-Path $install ('long-audio-build-history/'+([Guid]$plan.TransactionId).ToString('N'));Assert-LAPhysical $history
    if(-not $history.StartsWith($install+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'LongAudioBuildHistoryEscaped'}
    $sealed=Test-Path -LiteralPath (Join-Path $history 'plan.json')
    if($sealed){$null=Read-LABuildPin (Join-Path $history 'plan.json') $PlanSha}
    $old=if($sealed){Join-Path $history 'before'}else{$install}
    $markerBytes=Read-LABuildPin (Join-Path $old 'cloudscribe-install.json') $plan.PreviousMarkerSha256
    $parentBytes=Read-LABuildPin (Join-Path $old 'chirp-article-admission-policy.json') $plan.PreviousParentSha256
    $scopeBytes=Read-LABuildPin (Join-Path $old 'google-long-audio-scope.json') $plan.PreviousScopeSha256
    $ledgerPath=Join-Path $profile 'data/google-long-audio/budget-ledger.json'
    $ledgerBytes=Read-LABuildPin $(if($sealed){Join-Path $old 'budget-ledger.json'}else{$ledgerPath}) $plan.PreviousLedgerSha256
    $marker=ConvertFrom-LAJson $markerBytes;$parent=ConvertFrom-LAJson $parentBytes;$scope=ConvertFrom-LAJson $scopeBytes;$ledger=ConvertFrom-LAJson $ledgerBytes
    if($marker.Product -cne 'CloudScribe Pro' -or $marker.Profile -cne $profile -or $marker.ChirpArticlePolicySha256 -cne $plan.PreviousParentSha256 -or
        $marker.GoogleLongAudioScopeSha256 -cne $plan.PreviousScopeSha256 -or $scope.ParentPolicySha256 -cne $plan.PreviousParentSha256 -or
        $ledger.Version -ne 1 -or $ledger.ScopeSha256 -cne $plan.PreviousScopeSha256){throw 'LongAudioBuildPredecessorIdentity'}
    $newParent=Read-LABuildPin $plan.NewParentPath $plan.NewParentSha256;$newScope=Read-LABuildPin $plan.NewScopePath $plan.NewScopeSha256
    Assert-LABuildOnlyFields $parentBytes $newParent @('AppSha256','InfrastructureSha256','SourceApprovalSha256')
    $withoutDeferred=$plan.Version -eq 3 -and -not $scope.Contains('DeferredUnknownRanges') -and -not (ConvertFrom-LAJson $newScope).Contains('DeferredUnknownRanges')
    if($plan.Version -eq 1 -or $withoutDeferred){
        Assert-LABuildOnlyFields $scopeBytes $newScope @('ParentPolicySha256','SourceApprovalSha256')
        if($withoutDeferred -and $plan.DeferredEvidenceSha256 -cne (Get-LAHash ([Text.Encoding]::UTF8.GetBytes('null')))){throw 'LongAudioDeferredApprovalPin'}
    }
    else{
        $oldView=ConvertFrom-LAJson $scopeBytes;$newView=ConvertFrom-LAJson $newScope
        if(-not $oldView.Contains('DeferredUnknownRanges')){$oldView.DeferredUnknownRanges=$null}
        if(-not $newView.Contains('DeferredUnknownRanges')){throw 'LongAudioDeferredApprovalMissing'}
        Assert-LABuildOnlyFields (ConvertTo-LABytes $oldView) (ConvertTo-LABytes $newView) @('ParentPolicySha256','SourceApprovalSha256','DeferredUnknownRanges')
        Assert-LADeferredScope $newView
        Assert-LAExplicitRegenerationTransition $oldView $newView $ledger $profile
        if((Get-LAHash (ConvertTo-LABytes $newView.DeferredUnknownRanges)) -cne $plan.DeferredEvidenceSha256){throw 'LongAudioDeferredApprovalPin'}
        $evidence=@{Scope=$newView;Profile=$profile;Data=(Join-Path $profile 'data/google-long-audio')}
        Assert-LADeferredEvidence $evidence $ledger
    }
    $nextParent=ConvertFrom-LAJson $newParent;$nextScope=ConvertFrom-LAJson $newScope
    if($nextParent.SourceApprovalSha256 -cne $plan.SourceApprovalSha256 -or $nextScope.SourceApprovalSha256 -cne $plan.SourceApprovalSha256 -or
        $plan.SourceApprovalSha256 -ceq $parent.SourceApprovalSha256 -or $plan.SourceApprovalSha256 -ceq $scope.SourceApprovalSha256 -or
        $nextScope.ParentPolicySha256 -cne $plan.NewParentSha256){throw 'LongAudioBuildApprovalBinding'}
    if((Get-LABuildTreeHash (Join-Path $Package 'app')) -cne $plan.PackageAppTreeSha256 -or
        (Get-LAHash (Read-LABytes (Join-Path $Package 'app/CloudScribe.dll') 536870912)) -cne $nextParent.AppSha256 -or
        (Get-LAHash (Read-LABytes (Join-Path $Package 'app/CloudScribe.Infrastructure.dll') 536870912)) -cne $nextParent.InfrastructureSha256){throw 'LongAudioBuildPackageChanged'}
    $native=ConvertFrom-LAJson (Read-LABuildPin $plan.NativeProofPath $plan.NativeProofSha256)
    Assert-LAInteger $native.Version 1
    $nativeFields=@('Version','Passed','PreviousParentSha256','NewParentSha256','PackageAppTreeSha256','ProfileTreeSha256','LongClaimCount','LongHeldCents','ChirpCommittedCents','ChirpCeilingCents','ProviderCalls','LiveWrites')
    if($plan.Version -ge 2){$nativeFields+='DeferredEvidenceSha256'}
    if($plan.Version -eq 3){$nativeFields+=@('AdditiveRegistrySha256','AdditiveSidecarTreeSha256','PreviousLegacyPolicyIdentitySha256','NewLegacyPolicyIdentitySha256')}
    Assert-LARecord $native $nativeFields
    if($plan.Version -ge 2 -and $native.DeferredEvidenceSha256 -cne $plan.DeferredEvidenceSha256){throw 'LongAudioDeferredNativeProofPin'}
    if($plan.Version -eq 3){foreach($key in @('AdditiveRegistrySha256','AdditiveSidecarTreeSha256','PreviousLegacyPolicyIdentitySha256','NewLegacyPolicyIdentitySha256')){if($native[$key] -cne $plan[$key]){throw 'ArticleAdmissionBuildNativeProofChanged'}}}
    [long]$held=0;foreach($claim in $ledger.Claims){Assert-LAInteger $claim.MaximumUsdCents 1;$held += [long]$claim.MaximumUsdCents}
    if($native.Version -ne 1 -or $native.Passed -isnot [bool] -or -not $native.Passed -or $native.ProviderCalls -ne 0 -or $native.LiveWrites -ne 0 -or
        $native.PreviousParentSha256 -cne $plan.PreviousParentSha256 -or $native.NewParentSha256 -cne $plan.NewParentSha256 -or
        $native.PackageAppTreeSha256 -cne $plan.PackageAppTreeSha256 -or $native.ProfileTreeSha256 -cne $plan.ProfileTreeSha256 -or
        $native.LongClaimCount -ne $ledger.Claims.Count -or $native.LongHeldCents -ne $held -or
        $native.ChirpCommittedCents -ne $parent.Budget.ProjectCommittedMinorUnits -or $native.ChirpCeilingCents -ne $parent.Budget.ProjectCeilingMinorUnits){throw 'LongAudioBuildNativeProofChanged'}
    $ledger.ScopeSha256=$plan.NewScopeSha256;$newLedger=ConvertTo-LABytes $ledger
    $marker.AppSha256=$nextParent.AppSha256;$marker.ChirpArticlePolicySha256=$plan.NewParentSha256;$marker.GoogleLongAudioScopeSha256=$plan.NewScopeSha256
    $newMarker=ConvertTo-LABytes $marker
    $context=@{Install=$install;Profile=$profile;Package=$Package;History=$history;Plan=$plan;PlanPath=$PlanPath;PlanBytes=$planBytes;PlanSha=$PlanSha;Sealed=$sealed;
        Old=$old;OldMarker=$markerBytes;OldParent=$parentBytes;OldScope=$scopeBytes;OldLedger=$ledgerBytes;LedgerPath=$ledgerPath;
        NewMarker=$newMarker;NewParent=$newParent;NewScope=$newScope;NewLedger=$newLedger;Parent=$nextParent}
    $sidecar=Join-Path $install 'chirp-article-admissions'
    if($plan.Version -eq 3 -or (Test-Path -LiteralPath $sidecar)){
        if(-not (Get-Command Get-AABuildBinding -CommandType Function -ErrorAction SilentlyContinue)){throw 'ArticleAdmissionBuildHelperRequired'}
        $context.Additive=Get-AABuildBinding $context
    }else{$context.Additive=$null}
    return $context
}
