# Inert fixture only: no credentials, HTTP, real profile, installation or synthesis.
param($Repository,$Install,$Profile,$Checkpoint,$Rollback,$DeferredDescriptorPath,[switch]$Additive,[switch]$ConsumeAfterPublication)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1','BuildRebind.Transaction.ps1')){. (Join-Path $Repository ('tools/long-audio/'+$name))}
if($Additive){foreach($name in @('Admission.Validation.ps1','Admission.Transaction.ps1','BuildRebind.ps1')){. (Join-Path $Repository ('tools/article-admission/'+$name))}}
$fixture=[IO.Path]::GetDirectoryName($Profile)
$reviewInput=Join-Path $fixture ([Guid]::NewGuid().ToString('N')+'-synthetic-build-input')
[IO.Directory]::CreateDirectory($reviewInput)|Out-Null
$package=Join-Path $reviewInput 'package';[IO.Directory]::CreateDirectory((Join-Path $package 'app'))|Out-Null
[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.dll'),'updated inert synthetic app')
[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.Infrastructure.dll'),'updated inert synthetic infrastructure')
[IO.File]::WriteAllText((Join-Path $package 'app/appsettings.json'),'{}')
$parentPath=Join-Path $Install 'chirp-article-admission-policy.json';$scopePath=Join-Path $Install 'google-long-audio-scope.json'
$ledgerPath=Join-Path $Profile 'data/google-long-audio/budget-ledger.json'
$oldParent=Read-LABytes $parentPath;$oldScope=Read-LABytes $scopePath;$oldLedger=Read-LABytes $ledgerPath
$parent=ConvertFrom-LAJson $oldParent;$scope=ConvertFrom-LAJson $oldScope;$ledger=ConvertFrom-LAJson $oldLedger
$approvalBytes=[Text.Encoding]::UTF8.GetBytes('EXPLICITLY SYNTHETIC APPROVAL, NOT LIVE AUTHORITY '+[Guid]::NewGuid().ToString('N'))
$approval=Get-LAHash $approvalBytes
$parent.SourceApprovalSha256=$approval
$parent.AppSha256=Get-LAHash (Read-LABytes (Join-Path $package 'app/CloudScribe.dll'))
$parent.InfrastructureSha256=Get-LAHash (Read-LABytes (Join-Path $package 'app/CloudScribe.Infrastructure.dll'))
$nextParent=Join-Path $reviewInput 'parent.json';[IO.File]::WriteAllBytes($nextParent,(ConvertTo-LABytes $parent));$parentSha=Get-LAHash (Read-LABytes $nextParent)
$scope.ParentPolicySha256=$parentSha;$scope.SourceApprovalSha256=$approval
if($DeferredDescriptorPath){$scope.DeferredUnknownRanges=@((ConvertFrom-LAJson (Read-LABytes $DeferredDescriptorPath 8192)))}
$nextScope=Join-Path $reviewInput 'scope.json';[IO.File]::WriteAllBytes($nextScope,(ConvertTo-LABytes $scope))
$profileSha=Get-LABuildTreeHash $Profile;$packageSha=Get-LABuildTreeHash (Join-Path $package 'app')
$native=[ordered]@{Version=1;Passed=$true;PreviousParentSha256=(Get-LAHash $oldParent);NewParentSha256=$parentSha;
    PackageAppTreeSha256=$packageSha;ProfileTreeSha256=$profileSha;LongClaimCount=$ledger.Claims.Count;LongHeldCents=(@($ledger.Claims|Measure-Object MaximumUsdCents -Sum)[0].Sum);
    ChirpCommittedCents=$parent.Budget.ProjectCommittedMinorUnits;ChirpCeilingCents=$parent.Budget.ProjectCeilingMinorUnits;ProviderCalls=0;LiveWrites=0}
$nativePath=Join-Path $reviewInput 'EXPLICITLY-SYNTHETIC-native-proof.json';[IO.File]::WriteAllBytes($nativePath,(ConvertTo-LABytes $native))
$plan=[ordered]@{Version=1;TransactionId=[Guid]::NewGuid().ToString();SourceApprovalSha256=$approval;
    PreviousMarkerSha256=(Get-LAHash (Read-LABytes (Join-Path $Install 'cloudscribe-install.json')));PreviousParentSha256=(Get-LAHash $oldParent);
    PreviousScopeSha256=(Get-LAHash $oldScope);PreviousLedgerSha256=(Get-LAHash $oldLedger);
    PreviousAppTreeSha256=(Get-LABuildTreeHash (Join-Path $Install 'app'));PreviousProvisioningTreeSha256=(Get-LABuildTreeHash (Join-Path $Install 'long-audio-provisioning'));
    ProfileTreeSha256=$profileSha;PackageAppTreeSha256=$packageSha;NewParentPath=$nextParent;NewParentSha256=$parentSha;
    NewScopePath=$nextScope;NewScopeSha256=(Get-LAHash (Read-LABytes $nextScope));NativeProofPath=$nativePath;NativeProofSha256=(Get-LAHash (Read-LABytes $nativePath))}
if($DeferredDescriptorPath){
    $plan.Version=2;$plan.DeferredEvidenceSha256=Get-LAHash (ConvertTo-LABytes $scope.DeferredUnknownRanges)
    $native.DeferredEvidenceSha256=$plan.DeferredEvidenceSha256
    [IO.File]::WriteAllBytes($nativePath,(ConvertTo-LABytes $native));$plan.NativeProofSha256=Get-LAHash (Read-LABytes $nativePath)
}
if($Additive){
    $plan.Version=3;$root=Join-Path $Install 'chirp-article-admissions'
    if(-not $DeferredDescriptorPath){$plan.DeferredEvidenceSha256=Get-LAHash ([Text.Encoding]::UTF8.GetBytes('null'));$native.DeferredEvidenceSha256=$plan.DeferredEvidenceSha256}
    $oldRegistry=ConvertFrom-LAJson (Read-LABytes (Join-Path $root 'registry.json'))
    $plan.AdditiveRegistrySha256=Get-LAHash (Read-LABytes (Join-Path $root 'registry.json'))
    $plan.AdditiveSidecarTreeSha256=Get-LABuildTreeHash $root
    $plan.PreviousLegacyPolicyIdentitySha256=$oldRegistry.LegacyPolicySha256;$plan.NewLegacyPolicyIdentitySha256='b'*64
    $plan.SourceApprovalPath=Join-Path $reviewInput 'EXPLICITLY-SYNTHETIC-approval.txt';[IO.File]::WriteAllBytes($plan.SourceApprovalPath,$approvalBytes)
    foreach($key in @('AdditiveRegistrySha256','AdditiveSidecarTreeSha256','PreviousLegacyPolicyIdentitySha256','NewLegacyPolicyIdentitySha256')){$native[$key]=$plan[$key]}
    [IO.File]::WriteAllBytes($nativePath,(ConvertTo-LABytes $native));$plan.NativeProofSha256=Get-LAHash (Read-LABytes $nativePath)
}
$path=Join-Path $reviewInput 'plan.json';[IO.File]::WriteAllBytes($path,(ConvertTo-LABytes $plan));$sha=Get-LAHash (Read-LABytes $path)
if($DeferredDescriptorPath){
    foreach($bad in @('2',[double]2.0)){
        $invalid=ConvertFrom-LAJson (ConvertTo-LABytes $plan);$invalid.Version=$bad
        $invalidPath=Join-Path $reviewInput 'invalid-plan.json';[IO.File]::WriteAllBytes($invalidPath,(ConvertTo-LABytes $invalid))
        $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $invalidPath (Get-LAHash (Read-LABytes $invalidPath)) 'Inspect'|Out-Null}catch{$failed=$true}
        if(-not $failed){throw 'SyntheticCoercedDeferredPlanVersionAccepted'}
    }
    $nativeBytes=Read-LABytes $nativePath
    foreach($bad in @('1',[double]1.0)){
        $invalidNative=ConvertFrom-LAJson $nativeBytes;$invalidNative.Version=$bad
        [IO.File]::WriteAllBytes($nativePath,(ConvertTo-LABytes $invalidNative))
        $invalid=ConvertFrom-LAJson (ConvertTo-LABytes $plan);$invalid.NativeProofSha256=Get-LAHash (Read-LABytes $nativePath)
        $invalidPath=Join-Path $reviewInput 'invalid-native-plan.json';[IO.File]::WriteAllBytes($invalidPath,(ConvertTo-LABytes $invalid))
        $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $invalidPath (Get-LAHash (Read-LABytes $invalidPath)) 'Inspect'|Out-Null}catch{$failed=$true}
        if(-not $failed){throw 'SyntheticCoercedDeferredProofVersionAccepted'}
    }
    [IO.File]::WriteAllBytes($nativePath,$nativeBytes)
}
$stop={param($stage) if($stage -ceq $Checkpoint){throw 'SyntheticInterruption'}}.GetNewClosure()
try{Invoke-LABuildRebind $Install $Profile $package $path $sha 'Install' $stop;throw 'SyntheticExpectedInterruption'}
catch{if($_.Exception.Message -notlike '*SyntheticInterruption*'){throw}}
if($ConsumeAfterPublication){
    if(-not $Additive -or $Checkpoint -cne 'AdditiveRegistryPublished'){throw 'SyntheticPublicationCheckpointRequired'}
    $reservation=Join-Path $Profile 'data/synthetic-durable-additive-reservation'
    [IO.File]::WriteAllText($reservation,'Synthetic new-family SQL reservation after committed registry publication; must never restore to zero.')
}
$phase=if($Rollback -ceq 'True'){'Rollback'}else{'Resume'}
Invoke-LABuildRebind $Install $Profile $package $path $sha $phase|Out-Null
# Repeating a terminal command must not rewrite the receipt or alter history.
$history=Join-Path $Install ('long-audio-build-history/'+([Guid]$plan.TransactionId).ToString('N'))
$receipt=Join-Path $history 'receipt.json';$receiptTime=(Get-Item -LiteralPath $receipt).LastWriteTimeUtc
Invoke-LABuildRebind $Install $Profile $package $path $sha $phase|Out-Null
if((Get-Item -LiteralPath $receipt).LastWriteTimeUtc -ne $receiptTime){throw 'SyntheticTerminalReplayWrote'}
$after=ConvertFrom-LAJson (Read-LABytes $ledgerPath)
if($after.Claims.Count -ne $ledger.Claims.Count -or (ConvertTo-Json -InputObject $after.Claims -Depth 64 -Compress) -cne
    (ConvertTo-Json -InputObject $ledger.Claims -Depth 64 -Compress)){throw 'SyntheticClaimsChanged'}
$nowParent=ConvertFrom-LAJson (Read-LABytes $parentPath);$nowScope=ConvertFrom-LAJson (Read-LABytes $scopePath)
Assert-LABuildOnlyFields $oldParent (Read-LABytes $parentPath) @('AppSha256','InfrastructureSha256','SourceApprovalSha256')
if($DeferredDescriptorPath -and $Rollback -cne 'True'){
    $oldScopeView=ConvertFrom-LAJson $oldScope
    if(-not $oldScopeView.Contains('DeferredUnknownRanges')){$oldScopeView.DeferredUnknownRanges=$null}
    Assert-LABuildOnlyFields (ConvertTo-LABytes $oldScopeView) (Read-LABytes $scopePath) @('ParentPolicySha256','SourceApprovalSha256','DeferredUnknownRanges')
}else{Assert-LABuildOnlyFields $oldScope (Read-LABytes $scopePath) @('ParentPolicySha256','SourceApprovalSha256')}
if($Rollback -ceq 'True'){
    if((Get-LAHash (Read-LABytes $ledgerPath)) -cne $plan.PreviousLedgerSha256 -or
        (Get-LABuildTreeHash (Join-Path $Install 'long-audio-provisioning')) -cne $plan.PreviousProvisioningTreeSha256){throw 'SyntheticRollbackChangedHistory'}
}
if($Checkpoint -ceq 'Complete' -and $Rollback -ceq 'False'){
    $context=Get-LABuildRebind $Install $Profile $package $path $sha
    $proofPath=Join-Path $history 'new-provision-plan.json';$proofBytes=Read-LABytes $proofPath
    foreach($timestamp in @('2026-11-06T01:02:03.1234567Z','2026-11-06T01:02:03.1234567+00:00','2026-11-06T09:02:03.1234567+08:00')){
        $proof=ConvertFrom-LAJson $proofBytes;$proof.CreatedUtc=$timestamp;[IO.File]::WriteAllBytes($proofPath,(ConvertTo-LABytes $proof))
        $failed=$false;try{$null=Get-LABuildProofFiles $context}catch{$failed=$true}
        if($failed -ne $timestamp.EndsWith('+08:00',[StringComparison]::Ordinal)){throw 'SyntheticUtcValidationWrong'}
    }
    [IO.File]::WriteAllBytes($proofPath,$proofBytes)
    $liveReceipt=Join-Path $Install 'long-audio-provisioning/receipt.json';$saved=Read-LABytes $liveReceipt
    $corrupt=[Text.Encoding]::UTF8.GetBytes('UNRECOGNIZED SYNTHETIC PROOF BYTES');[IO.File]::WriteAllBytes($liveReceipt,$corrupt)
    $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $path $sha 'Resume'|Out-Null}catch{$failed=$true}
    if(-not $failed -or (Get-LAHash (Read-LABytes $liveReceipt)) -cne (Get-LAHash $corrupt)){throw 'SyntheticUnknownProofOverwritten'}
    [IO.File]::WriteAllBytes($liveReceipt,$saved)
    $extra=Join-Path $Install 'long-audio-provisioning/unknown-evidence.json';[IO.File]::WriteAllText($extra,'synthetic evidence')
    $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $path $sha 'Resume'|Out-Null}catch{$failed=$true}
    if(-not $failed -or -not (Test-Path -LiteralPath $extra)){throw 'SyntheticExtraProofNotPreserved'}
    [IO.File]::Delete($extra)
    $retained=Read-LABytes $ledgerPath;$changed=ConvertFrom-LAJson $retained;$changed.Claims[0].MaximumUsdCents++
    $changedBytes=ConvertTo-LABytes $changed;[IO.File]::WriteAllBytes($ledgerPath,$changedBytes)
    $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $path $sha 'Rollback'|Out-Null}catch{$failed=$true}
    if(-not $failed -or (Get-LAHash (Read-LABytes $ledgerPath)) -cne (Get-LAHash $changedBytes)){throw 'SyntheticChangedFinancialHistoryOverwritten'}
    [IO.File]::WriteAllBytes($ledgerPath,$retained)
}
if($DeferredDescriptorPath -and $Rollback -cne 'True'){
    Invoke-LABuildRebind $Install $Profile $package $path $sha 'Rollback'|Out-Null
    if((Get-LAHash (Read-LABytes $ledgerPath)) -cne $plan.PreviousLedgerSha256 -or
        (Get-LAHash (Read-LABytes $scopePath)) -cne $plan.PreviousScopeSha256){throw 'SyntheticDeferredRollbackChangedHistory'}
}
if($Additive){
    $registry=ConvertFrom-LAJson (Read-LABytes (Join-Path $Install 'chirp-article-admissions/registry.json'))
    if((ConvertTo-Json -InputObject $registry.Families -Depth 64 -Compress) -cne (ConvertTo-Json -InputObject $oldRegistry.Families -Depth 64 -Compress)){throw 'SyntheticAdditiveFamiliesChanged'}
    foreach($family in $registry.Families){$null=Get-AAHashPin (Join-Path $Install ('chirp-article-admissions/grants/'+$family.GrantSha256+'.json')) $family.GrantSha256}
    if($Rollback -cne 'True'){
        $failed=$false;try{Invoke-LABuildRebind $Install $Profile $package $path $sha 'Rollback'|Out-Null}catch{$failed=$true}
        if(-not $failed){throw 'SyntheticPublishedAdditiveBuildRollbackAccepted'}
    }
    if($ConsumeAfterPublication -and -not (Test-Path -LiteralPath $reservation)){throw 'SyntheticBuildResetNewReservation'}
}
'SyntheticBuildRebindVerified: 0 provider calls, 0 new commitments, exact retained claim and authority.'
