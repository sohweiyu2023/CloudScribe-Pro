# Build-only receipt renewal. Original family policy and financial authorization identities never change.
function Get-AABuildBinding($Context) {
    $c=$Context;$root=Join-Path $c.Install 'chirp-article-admissions'
    if($c.Plan.Version -ne 3){if(Test-Path -LiteralPath $root){throw 'ArticleAdmissionExplicitBuildRebindRequired'};return $null}
    $p=$c.Plan;$oldRoot=if($c.Sealed){Join-Path $c.History 'before/chirp-article-admissions'}else{$root}
    Assert-AASidecar $oldRoot
    if((Get-LABuildTreeHash $oldRoot) -cne $p.AdditiveSidecarTreeSha256){throw 'ArticleAdmissionBuildBaselineChanged'}
    $old=Read-AARegistry $oldRoot $p.AdditiveRegistrySha256
    foreach($entry in $old.Value.Families){
        $family=ConvertFrom-LAJson (Get-AAHashPin (Join-Path $oldRoot ('grants/'+$entry.GrantSha256+'.json')) $entry.GrantSha256)
        if($family.Policy.SourceApprovalSha256 -ceq $p.SourceApprovalSha256){throw 'ArticleAdmissionReusedBuildSourceApproval'}
    }
    if($old.Value.LegacyPolicySha256 -cne $p.PreviousLegacyPolicyIdentitySha256 -or
        $old.Binding.InstallMarkerSha256 -cne $p.PreviousMarkerSha256 -or $old.Binding.AppSha256 -cne (ConvertFrom-LAJson $c.OldParent).AppSha256 -or
        $old.Binding.InfrastructureSha256 -cne (ConvertFrom-LAJson $c.OldParent).InfrastructureSha256 -or $old.Binding.ProfileRootSha256 -cne $c.Parent.ProfileRootSha256){throw 'ArticleAdmissionBuildPreviousBinding'}
    foreach($entry in @(Get-ChildItem -LiteralPath (Join-Path $oldRoot 'transactions') -Directory -Force -ErrorAction SilentlyContinue)){
        $state=ConvertFrom-LAJson (Read-LABytes (Join-Path $entry.FullName 'state.json') 16384)
        if($state.Phase -cnotin @('Complete','RolledBack')){throw 'ArticleAdmissionBuildCompetingTransition'}
    }
    $source=Get-AAHashPin $p.SourceApprovalPath $p.SourceApprovalSha256
    $native=Read-LABuildPin $p.NativeProofPath $p.NativeProofSha256
    $receipt=[ordered]@{SchemaVersion=1;TransactionId=$p.TransactionId;Operation='Rebind';PlanSha256=$c.PlanSha;PreviousRegistrySha256=$p.AdditiveRegistrySha256;
        FamilyId=$null;GrantSha256=$null;ProfileRootSha256=$c.Parent.ProfileRootSha256;AppSha256=$c.Parent.AppSha256;InfrastructureSha256=$c.Parent.InfrastructureSha256;
        InstallMarkerSha256=(Get-LAHash $c.NewMarker);LegacyPolicySha256=$p.NewLegacyPolicyIdentitySha256;SourceApprovalSha256=$p.SourceApprovalSha256;NativeProofSha256=$p.NativeProofSha256;Phase='Complete'}
    $receiptBytes=ConvertTo-LABytes $receipt;$sha=Get-LAHash $receiptBytes
    $registry=[ordered]@{SchemaVersion=1;LegacyPolicySha256=$p.NewLegacyPolicyIdentitySha256;Families=@($old.Value.Families);BindingReceiptSha256=$sha}
    $registryBytes=ConvertTo-LABytes $registry
    $entries=@(@('plans',$c.PlanBytes,'json'),@('native-proofs',$native,'json'),@('source-approvals',$source,'bin'),
        @('receipts',$receiptBytes,'json'),@('registries',$old.Bytes,'json'),@('registries',$registryBytes,'json'))
    return @{Root=$root;OldRoot=$oldRoot;OldRegistryBytes=$old.Bytes;NewRegistryBytes=$registryBytes;NewRegistrySha=(Get-LAHash $registryBytes);Entries=$entries}
}
function Assert-AABuildKnown($Context) {
    if(-not $Context.Contains('Additive') -or $null -eq $Context.Additive){return}
    $a=$Context.Additive;Assert-AASidecar $a.Root
    $current=Get-LAHash (Read-LABytes (Join-Path $a.Root 'registry.json'))
    if($current -cnotin @($Context.Plan.AdditiveRegistrySha256,$a.NewRegistrySha)){throw 'ArticleAdmissionUnknownBuildRegistry'}
    $expected=@{};foreach($row in (Get-LABuildInventory $a.OldRoot)){$expected[$row.Path]=$row.Sha256}
    foreach($entry in $a.Entries){$expected[$entry[0]+'/'+(Get-LAHash $entry[1])+'.'+$entry[2]]=Get-LAHash $entry[1]}
    $live=Get-LABuildInventory $a.Root
    foreach($row in $live){
        if($row.Path -ceq 'registry.json'){continue}
        if(-not $expected.ContainsKey($row.Path) -or $expected[$row.Path] -cne $row.Sha256){throw 'ArticleAdmissionUnknownBuildSidecar'}
    }
    foreach($row in (Get-LABuildInventory $a.OldRoot)){
        if($row.Path -ceq 'registry.json'){continue}
        if(@($live|Where-Object {$_.Path -ceq $row.Path -and $_.Sha256 -ceq $row.Sha256}).Count -ne 1){throw 'ArticleAdmissionBuildHistoryRemoved'}
    }
}
function Publish-AABuildBinding($Context) {
    if($null -eq $Context.Additive){return}
    $a=$Context.Additive;Assert-AABuildKnown $Context
    foreach($entry in $a.Entries){Set-AAImmutable $a.Root $entry[0] $entry[1] $entry[2]}
    Write-LAAtomic (Join-Path $a.Root 'registry.json') $a.NewRegistryBytes -Replace
    Assert-AABuildKnown $Context
    $null=Read-AARegistry $a.Root $a.NewRegistrySha
}
function Assert-AABuildFinished($Context,[switch]$Rollback) {
    if(-not $Context.Contains('Additive') -or $null -eq $Context.Additive){return}
    Assert-AABuildKnown $Context
    $expected=if($Rollback){$Context.Plan.AdditiveRegistrySha256}else{$Context.Additive.NewRegistrySha}
    $null=Read-AARegistry $Context.Additive.Root $expected
}
