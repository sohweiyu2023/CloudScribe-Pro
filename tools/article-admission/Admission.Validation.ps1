# Private additive admission uses a sidecar. Never add fields to the authenticated install marker.
function Get-AAHashPin([string]$Path,[string]$Sha,[long]$Maximum=8388608) {
    Assert-LAHash $Sha;$bytes=Read-LABytes $Path $Maximum
    if((Get-LAHash $bytes) -cne $Sha){throw 'ArticleAdmissionInputChanged'}
    return ,$bytes
}
function Get-AARoot([string]$Path) {
    Assert-LAPhysical $Path
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}
function Assert-AASeparate([string]$Left,[string]$Right) {
    foreach($pair in @(@($Left,$Right),@($Right,$Left))){
        if($pair[0] -ieq $pair[1] -or $pair[0].StartsWith($pair[1]+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'ArticleAdmissionOverlappingRoots'}
    }
}
function Read-AARegistry([string]$Root,[string]$ExpectedSha) {
    $bytes=Get-AAHashPin (Join-Path $Root 'registry.json') $ExpectedSha
    $registry=ConvertFrom-LAJson $bytes
    Assert-LARecord $registry @('SchemaVersion','LegacyPolicySha256','Families','BindingReceiptSha256')
    Assert-LAInteger $registry.SchemaVersion 1
    if($registry.SchemaVersion -ne 1 -or $registry.Families -isnot [array] -or $registry.Families.Count -eq 0 -or $registry.Families.Count -gt 32){throw 'ArticleAdmissionRegistryShape'}
    Assert-LAHash $registry.LegacyPolicySha256;Assert-LAHash $registry.BindingReceiptSha256
    $ids=[Collections.Generic.HashSet[Guid]]::new();$grants=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($family in $registry.Families){
        Assert-LARecord $family @('FamilyId','GrantSha256','ReceiptSha256')
        if([Guid]$family.FamilyId -eq [Guid]::Empty -or -not $ids.Add([Guid]$family.FamilyId) -or -not $grants.Add([string]$family.GrantSha256)){throw 'ArticleAdmissionDuplicateFamily'}
        $grant=ConvertFrom-LAJson (Get-AAHashPin (Join-Path $Root ('grants/'+$family.GrantSha256+'.json')) $family.GrantSha256)
        $receipt=ConvertFrom-LAJson (Get-AAHashPin (Join-Path $Root ('receipts/'+$family.ReceiptSha256+'.json')) $family.ReceiptSha256)
        Assert-AAReceipt $receipt
        if($grant.FamilyId -cne $family.FamilyId -or $receipt.FamilyId -cne $family.FamilyId -or $receipt.GrantSha256 -cne $family.GrantSha256 -or $receipt.Operation -cne 'Admit'){throw 'ArticleAdmissionRegistryBinding'}
    }
    $binding=ConvertFrom-LAJson (Get-AAHashPin (Join-Path $Root ('receipts/'+$registry.BindingReceiptSha256+'.json')) $registry.BindingReceiptSha256)
    Assert-AAReceipt $binding
    if($binding.LegacyPolicySha256 -cne $registry.LegacyPolicySha256){throw 'ArticleAdmissionLegacyBinding'}
    return @{Bytes=$bytes;Value=$registry;Binding=$binding}
}
function Assert-AAReceipt($Receipt) {
    Assert-LARecord $Receipt @('SchemaVersion','TransactionId','Operation','PlanSha256','PreviousRegistrySha256','FamilyId','GrantSha256','ProfileRootSha256','AppSha256','InfrastructureSha256','InstallMarkerSha256','LegacyPolicySha256','SourceApprovalSha256','NativeProofSha256','Phase')
    Assert-LAInteger $Receipt.SchemaVersion 1
    if($Receipt.SchemaVersion -ne 1 -or [Guid]$Receipt.TransactionId -eq [Guid]::Empty -or $Receipt.Operation -cnotin @('Admit','Rebind') -or $Receipt.Phase -cne 'Complete'){throw 'ArticleAdmissionReceiptShape'}
    foreach($key in @('PlanSha256','ProfileRootSha256','AppSha256','InfrastructureSha256','InstallMarkerSha256','LegacyPolicySha256','SourceApprovalSha256','NativeProofSha256')){Assert-LAHash $Receipt[$key]}
    if($null -ne $Receipt.PreviousRegistrySha256){Assert-LAHash $Receipt.PreviousRegistrySha256}
    if($Receipt.Operation -ceq 'Admit'){Assert-LAHash $Receipt.GrantSha256;if([Guid]$Receipt.FamilyId -eq [Guid]::Empty){throw 'ArticleAdmissionFamilyIdentity'}}
    elseif($null -ne $Receipt.FamilyId -or $null -ne $Receipt.GrantSha256 -or $null -eq $Receipt.PreviousRegistrySha256){throw 'ArticleAdmissionRebindShape'}
}
function Assert-AASidecar([string]$Root) {
    if(-not (Test-Path -LiteralPath $Root)){return}
    Assert-LAPhysical $Root
    $folders=@('grants','receipts','plans','native-proofs','source-approvals','registries','transactions')
    foreach($entry in @(Get-ChildItem -LiteralPath $Root -Force)){
        Assert-LAPhysical $entry.FullName
        if(-not $entry.PSIsContainer){if($entry.Name -cne 'registry.json'){throw 'ArticleAdmissionUnknownSidecarFile'};continue}
        if($entry.Name -cnotin $folders){throw 'ArticleAdmissionUnknownSidecarDirectory'}
        foreach($child in @(Get-ChildItem -LiteralPath $entry.FullName -Force -Recurse)){
            Assert-LAPhysical $child.FullName
            if($entry.Name -ceq 'transactions'){continue}
            if($child.PSIsContainer -or $child.Name -cnotmatch '^[a-f0-9]{64}\.(json|bin)$' -or
                (Get-FileHash -LiteralPath $child.FullName).Hash.ToLowerInvariant() -cne $child.BaseName){throw 'ArticleAdmissionImmutableFileChanged'}
        }
    }
}
function Assert-AAFamily($Family,$Plan,[string]$Profile) {
    Assert-LARecord $Family @('FamilyId','Policy','AllInCeilingMinorUnits','ProvisionMinorUnits','ReviewedBaselineSha256','Requests')
    if([Guid]$Family.FamilyId -eq [Guid]::Empty){throw 'ArticleAdmissionFamilyIdentity'}
    Assert-LAInteger $Family.AllInCeilingMinorUnits 1;Assert-LAInteger $Family.ProvisionMinorUnits
    if($Family.ProvisionMinorUnits -ge $Family.AllInCeilingMinorUnits){throw 'ArticleAdmissionAllInCeiling'}
    Assert-LAHash $Family.ReviewedBaselineSha256
    $p=$Family.Policy;$b=$p.Budget
    $raw=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString((ConvertTo-LABytes $p)))
    try{Assert-ChirpPolicy $raw.RootElement}finally{$raw.Dispose()}
    if($p.SchemaVersion -ne 3 -or $p.AppSha256 -cne $Plan.AppSha256 -or $p.InfrastructureSha256 -cne $Plan.InfrastructureSha256 -or
        $p.ProfileRootSha256 -cne $Plan.ProfileRootSha256 -or $p.SourceApprovalSha256 -cne $Plan.SourceApprovalSha256 -or
        $p.Currency -cne 'USD' -or $p.Scale -ne 2 -or $b.ReviewedEvidenceSha256 -cne $Family.ReviewedBaselineSha256 -or
        $b.ProjectCommittedMinorUnits -ne $Family.ProvisionMinorUnits -or $b.AllocationCommittedMinorUnits -ne $Family.ProvisionMinorUnits -or
        $b.ProjectCeilingMinorUnits -ne $Family.AllInCeilingMinorUnits -or $b.AllocationCeilingMinorUnits -ne $Family.AllInCeilingMinorUnits -or
        $p.MaximumAdditionalReservationMinorUnits -gt $Family.AllInCeilingMinorUnits-$Family.ProvisionMinorUnits -or -not $p.Contains('WholeArticleGeneration') -or $null -eq $p.WholeArticleGeneration){throw 'ArticleAdmissionFreshAllowance'}
    foreach($key in @('SuccessorGrant','DiagnosticEvidence','SentenceRecovery','RecoveryContinuation','RecoveryLimitRevision','CompletionContinuity','LegacyAssociations','LegacyAssociationRows')){
        if($p.Contains($key) -and $null -ne $p[$key]){throw 'ArticleAdmissionHistoricalAuthorityForbidden'}
    }
    foreach($rows in @($b.QueueRows,$b.SpendRows,$b.RecoveryRows,$b.TransitionRows,$p.ScopeRows,$p.PriorArticleAttemptRows,$p.PriorSuccessorAttemptRows)){
        if($null -eq $rows -or $rows.Count -ne 0){throw 'ArticleAdmissionFreshBaselineRequired'}
    }
    if($p.Articles.Count -ne 2 -or @($p.Articles|ForEach-Object {$_.Proposal.DocumentId}|Select-Object -Unique).Count -ne 2 -or
        @($p.Articles|ForEach-Object {$_.Proposal.SourceSha256}|Select-Object -Unique).Count -ne 1 -or
        @($p.Articles|Where-Object {$_.Proposal.VoiceName -ceq 'en-US-Neural2-D'}).Count -ne 1 -or
        @($p.Articles|Where-Object {$_.Proposal.VoiceName -ceq 'en-US-Chirp3-HD-Aoede'}).Count -ne 1){throw 'ArticleAdmissionExactComparisonRequired'}
    $pins=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$expected=0
    foreach($article in $p.Articles){
        $proposal=$article.Proposal
        if($proposal.LanguageCode -cne 'en-US' -or $article.ReusedAudio.Count -ne 0 -or $article.Lineage.History.Count -ne 1 -or
            $article.Lineage.History[0].Attempts.Count -ne 0 -or $article.AllowedSegments.Count -ne $proposal.Parts.Count){throw 'ArticleAdmissionFreshComparisonRequired'}
        $history=$article.Lineage.History[0]
        if($history.DocumentId -cne $proposal.DocumentId -or $history.RevisionId -cne $proposal.RevisionId -or $history.StartScalar -ne 0 -or
            $history.EndScalarExclusive -ne $proposal.SourceScalars -or $history.TextSha256 -cne $proposal.SourceSha256){throw 'ArticleAdmissionFreshLineageChanged'}
        foreach($key in @('Refinement','SentenceSuccessor')){if($proposal.Contains($key) -and $null -ne $proposal[$key]){throw 'ArticleAdmissionFreshComparisonRequired'}}
        $source=Join-Path $Profile ('documents/'+([Guid]$proposal.DocumentId).ToString('N')+'/content/'+([Guid]$proposal.RevisionId).ToString('N')+'.utf8')
        $null=Get-AAHashPin $source $proposal.SourceSha256 4000000
        foreach($part in $proposal.Parts){
            $expected++;$pin=@($Family.Requests|Where-Object {$_.PlanSha256 -ceq $proposal.PlanSha256 -and $_.CoverageOrdinal -eq $part.CoverageOrdinal})
            $limit=@($article.AllowedSegments|Where-Object {$_.CoverageOrdinal -eq $part.CoverageOrdinal})
            if($pin.Count -ne 1 -or $limit.Count -ne 1 -or $null -ne $part.Range.ReusedReceiptSha256){throw 'ArticleAdmissionRequestMapping'}
            Assert-LARecord $pin[0] @('PlanSha256','CoverageOrdinal','Envelope','EstimateMinorUnits')
            $e=$pin[0].Envelope;Assert-LAInteger $pin[0].EstimateMinorUnits
            $fields=@('AccountId','CredentialReferenceId','CapabilityProvenanceId','PricingProvenanceId','RequestRevision','VoiceName','AudioEncoding','CompiledPayloadSha256','CompiledPayloadBytes','RecoveryAuthorizationId')
            if($e.Contains('ChirpAuthorizationId')){$fields+='ChirpAuthorizationId'}
            Assert-LARecord $e $fields;Assert-LAInteger $e.RequestRevision
            if(-not $pins.Add($pin[0].PlanSha256+':'+$pin[0].CoverageOrdinal) -or $pin[0].EstimateMinorUnits -gt $limit[0].MaximumMinorUnits -or
                $e.AccountId -cne $p.AccountId -or $e.VoiceName -cne $proposal.VoiceName -or $e.AudioEncoding -cne 'MP3' -or
                $e.CompiledPayloadSha256 -cne $part.PayloadSha256 -or $e.CompiledPayloadBytes -ne $part.PayloadBytes -or
                [string]::IsNullOrWhiteSpace($e.CredentialReferenceId) -or [string]::IsNullOrWhiteSpace($e.CapabilityProvenanceId) -or [string]::IsNullOrWhiteSpace($e.PricingProvenanceId) -or
                $null -ne $e.RecoveryAuthorizationId -or ($e.Contains('ChirpAuthorizationId') -and $null -ne $e.ChirpAuthorizationId)){throw 'ArticleAdmissionExactRequestPin'}
        }
    }
    if($Family.Requests.Count -ne $expected){throw 'ArticleAdmissionUnmappedRequest'}
}
function Get-AAAdmission([string]$Install,[string]$Profile,[string]$PlanPath,[string]$PlanSha) {
    $install=Get-AARoot $Install;$profile=Get-AARoot $Profile;Assert-AASeparate $install $profile
    $planBytes=Get-AAHashPin $PlanPath $PlanSha;$plan=ConvertFrom-LAJson $planBytes
    Assert-LARecord $plan @('SchemaVersion','TransactionId','InstallRoot','ProfileRoot','ProfileRootSha256','AppSha256','InfrastructureSha256',
        'SourceApprovalPath','SourceApprovalSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256','LegacyScopeSha256','LegacyLedgerSha256',
        'ProfileTreeSha256','RetainedEvidenceTreeSha256','PreviousRegistrySha256','PreviousSidecarTreeSha256','GrantPath','GrantSha256','NativeProofPath','NativeProofSha256')
    Assert-LAInteger $plan.SchemaVersion 1
    if($plan.SchemaVersion -ne 1 -or [Guid]$plan.TransactionId -eq [Guid]::Empty -or $plan.InstallRoot -cne $install -or $plan.ProfileRoot -cne $profile){throw 'ArticleAdmissionPlanShape'}
    foreach($key in $plan.Keys|Where-Object {$_ -like '*Sha256'}){
        if($key -cin @('PreviousRegistrySha256','PreviousSidecarTreeSha256') -and $null -eq $plan[$key]){continue};Assert-LAHash $plan[$key]
    }
    if(($null -eq $plan.PreviousRegistrySha256) -ne ($null -eq $plan.PreviousSidecarTreeSha256)){throw 'ArticleAdmissionPreviousRegistryRequired'}
    foreach($path in @($PlanPath,$plan.GrantPath,$plan.NativeProofPath,$plan.SourceApprovalPath)){
        $inputRoot=Get-AARoot $path;Assert-AASeparate $inputRoot $install;Assert-AASeparate $inputRoot $profile
    }
    if((Get-LAHash ([Text.Encoding]::UTF8.GetBytes($profile.ToUpperInvariant()))) -cne $plan.ProfileRootSha256){throw 'ArticleAdmissionProfileIdentity'}
    $markerBytes=Get-AAHashPin (Join-Path $install 'cloudscribe-install.json') $plan.InstallMarkerSha256 16384;$marker=ConvertFrom-LAJson $markerBytes
    if($marker.Product -cne 'CloudScribe Pro' -or $marker.Profile -cne $profile -or $marker.AppSha256.ToLowerInvariant() -cne $plan.AppSha256 -or
        $marker.ChirpArticlePolicySha256 -cne $plan.LegacyPolicyFileSha256 -or $marker.GoogleLongAudioScopeSha256 -cne $plan.LegacyScopeSha256 -or
        ($marker.Contains('LongAudioBuildRebindPending') -and $marker.LongAudioBuildRebindPending)){throw 'ArticleAdmissionInstalledIdentity'}
    $null=Get-AAHashPin (Join-Path $install 'app/CloudScribe.dll') $plan.AppSha256 536870912
    $null=Get-AAHashPin (Join-Path $install 'app/CloudScribe.Infrastructure.dll') $plan.InfrastructureSha256 536870912
    $null=Get-AAHashPin (Join-Path $install 'chirp-article-admission-policy.json') $plan.LegacyPolicyFileSha256
    $null=Get-AAHashPin (Join-Path $install 'google-long-audio-scope.json') $plan.LegacyScopeSha256
    $legacy=Get-LAContext $install $profile (Join-Path $install 'google-long-audio-scope.json') $plan.LegacyScopeSha256
    if((ConvertFrom-LAJson $legacy.ParentBytes).SourceApprovalSha256 -ceq $plan.SourceApprovalSha256){throw 'ArticleAdmissionReusedSourceApproval'}
    $provision=Read-LAPlan $legacy
    if($provision.Receipt.Phase -cne 'Complete'){throw 'ArticleAdmissionIncompleteRetainedInstallation'}
    Assert-LACompleted $legacy $provision
    foreach($transition in @(Get-ChildItem -LiteralPath (Join-Path $install 'long-audio-build-history') -Directory -Force -ErrorAction SilentlyContinue)){
        Assert-LAPhysical $transition.FullName
        $buildState=ConvertFrom-LAJson (Read-LABytes (Join-Path $transition.FullName 'receipt.json') 16384)
        if($buildState.Phase -cnotin @('Complete','RolledBack')){throw 'ArticleAdmissionCompetingBuildTransition'}
    }
    $grantBytes=Get-AAHashPin $plan.GrantPath $plan.GrantSha256;$family=ConvertFrom-LAJson $grantBytes
    $sourceBytes=Get-AAHashPin $plan.SourceApprovalPath $plan.SourceApprovalSha256
    $nativeBytes=Get-AAHashPin $plan.NativeProofPath $plan.NativeProofSha256;$native=ConvertFrom-LAJson $nativeBytes
    Assert-LARecord $native @('SchemaVersion','Passed','AppSha256','InfrastructureSha256','ProfileRootSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256',
        'LegacyScopeSha256','LegacyLedgerSha256','ProfileTreeSha256','RetainedEvidenceTreeSha256','GrantSha256','SourceApprovalSha256','FamilyId','ReviewedBaselineSha256','FamilyPolicySha256','AllInCeilingMinorUnits','ProvisionMinorUnits','ProviderCalls','LiveWrites')
    foreach($key in @('AppSha256','InfrastructureSha256','ProfileRootSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256','LegacyScopeSha256','LegacyLedgerSha256','ProfileTreeSha256','RetainedEvidenceTreeSha256','GrantSha256','SourceApprovalSha256')){
        if($native[$key] -cne $plan[$key]){throw 'ArticleAdmissionNativeProofChanged'}
    }
    Assert-LAHash $native.FamilyPolicySha256
    Assert-LAInteger $native.SchemaVersion 1;Assert-LAInteger $native.ProviderCalls;Assert-LAInteger $native.LiveWrites
    # Grant authors serialize the reviewed typed family with the candidate's canonical serializer.
    # Its exact compact policy subtree must be the policy identity pinned by the native validator.
    $grantJson=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($grantBytes))
    try{if((Get-LAHash ([Text.Encoding]::UTF8.GetBytes($grantJson.RootElement.GetProperty('Policy').GetRawText()))) -cne $native.FamilyPolicySha256){throw 'ArticleAdmissionNativePolicyIdentity'}}finally{$grantJson.Dispose()}
    if($native.SchemaVersion -ne 1 -or $native.Passed -isnot [bool] -or -not $native.Passed -or $native.ProviderCalls -ne 0 -or $native.LiveWrites -ne 0 -or
        $native.FamilyId -cne $family.FamilyId -or $native.ReviewedBaselineSha256 -cne $family.ReviewedBaselineSha256 -or
        $native.AllInCeilingMinorUnits -ne $family.AllInCeilingMinorUnits -or $native.ProvisionMinorUnits -ne $family.ProvisionMinorUnits){throw 'ArticleAdmissionNativeProofChanged'}
    $root=Join-Path $install 'chirp-article-admissions';Assert-AASidecar $root
    $transaction=Join-Path $root ('transactions/'+([Guid]$plan.TransactionId).ToString('N'));Assert-LAPhysical $transaction
    $statePath=Join-Path $transaction 'state.json';$state=$null
    if(Test-Path -LiteralPath $statePath){
        $state=ConvertFrom-LAJson (Read-LABytes $statePath 16384)
        Assert-LARecord $state @('SchemaVersion','TransactionId','PlanSha256','Phase')
        Assert-LAInteger $state.SchemaVersion 1
        if($state.SchemaVersion -ne 1 -or $state.TransactionId -cne $plan.TransactionId -or $state.PlanSha256 -cne $PlanSha -or
            $state.Phase -cnotin @('Owned','PlanSealed','InputsSealed','ReceiptSealed','RegistryPublished','Complete','RolledBack')){throw 'ArticleAdmissionTransactionChanged'}
        if(@(Get-ChildItem -LiteralPath $transaction -Force|Where-Object {$_.Name -cne 'state.json'}).Count){throw 'ArticleAdmissionUnknownTransactionFile'}
    }elseif(Test-Path -LiteralPath $transaction){throw 'ArticleAdmissionUnownedPartialState'}
    $transactions=Join-Path $root 'transactions'
    foreach($other in @(Get-ChildItem -LiteralPath $transactions -Force -ErrorAction SilentlyContinue)){
        if(-not $other.PSIsContainer -or $other.Name -cnotmatch '^[a-f0-9]{32}$'){throw 'ArticleAdmissionUnknownTransaction'}
        if($other.FullName -ceq $transaction){continue}
        $otherState=ConvertFrom-LAJson (Read-LABytes (Join-Path $other.FullName 'state.json') 16384)
        Assert-LARecord $otherState @('SchemaVersion','TransactionId','PlanSha256','Phase')
        if($otherState.Phase -cnotin @('Complete','RolledBack')){throw 'ArticleAdmissionCompetingTransition'}
    }
    $receipt=[ordered]@{SchemaVersion=1;TransactionId=$plan.TransactionId;Operation='Admit';PlanSha256=$PlanSha;PreviousRegistrySha256=$plan.PreviousRegistrySha256;
        FamilyId=$family.FamilyId;GrantSha256=$plan.GrantSha256;ProfileRootSha256=$plan.ProfileRootSha256;AppSha256=$plan.AppSha256;InfrastructureSha256=$plan.InfrastructureSha256;
        InstallMarkerSha256=$plan.InstallMarkerSha256;LegacyPolicySha256=$plan.LegacyPolicySha256;SourceApprovalSha256=$plan.SourceApprovalSha256;NativeProofSha256=$plan.NativeProofSha256;Phase='Complete'}
    $receiptBytes=ConvertTo-LABytes $receipt;$receiptSha=Get-LAHash $receiptBytes
    $families=@();$oldRegistryBytes=$null
    if($null -ne $plan.PreviousRegistrySha256){
        $snapshotPath=Join-Path $root ('registries/'+$plan.PreviousRegistrySha256+'.json')
        $oldPath=if(Test-Path -LiteralPath $snapshotPath){$snapshotPath}else{Join-Path $root 'registry.json'}
        $oldRegistryBytes=Get-AAHashPin $oldPath $plan.PreviousRegistrySha256;$oldRegistry=ConvertFrom-LAJson $oldRegistryBytes
        $families=@($oldRegistry.Families)
        if($oldRegistry.LegacyPolicySha256 -cne $plan.LegacyPolicySha256 -or @($families|Where-Object {$_.FamilyId -ceq $family.FamilyId -or $_.GrantSha256 -ceq $plan.GrantSha256}).Count){throw 'ArticleAdmissionExistingFamilyChanged'}
        foreach($prior in $families){
            $priorGrant=ConvertFrom-LAJson (Get-AAHashPin (Join-Path $root ('grants/'+$prior.GrantSha256+'.json')) $prior.GrantSha256)
            if($priorGrant.Policy.SourceApprovalSha256 -ceq $plan.SourceApprovalSha256){throw 'ArticleAdmissionReusedSourceApproval'}
            foreach($article in $family.Policy.Articles){
                if(@($priorGrant.Policy.Articles|Where-Object {$_.Proposal.DocumentId -ceq $article.Proposal.DocumentId -or $_.Proposal.PlanSha256 -ceq $article.Proposal.PlanSha256}).Count){throw 'ArticleAdmissionExistingDocumentReused'}
            }
        }
    }
    $families+=@([ordered]@{FamilyId=$family.FamilyId;GrantSha256=$plan.GrantSha256;ReceiptSha256=$receiptSha})
    $newRegistry=[ordered]@{SchemaVersion=1;LegacyPolicySha256=$plan.LegacyPolicySha256;Families=$families;BindingReceiptSha256=$receiptSha}
    $newRegistryBytes=ConvertTo-LABytes $newRegistry;$newRegistrySha=Get-LAHash $newRegistryBytes
    $registryPath=Join-Path $root 'registry.json';$published=$false
    if(Test-Path -LiteralPath $registryPath){
        $liveSha=Get-LAHash (Read-LABytes $registryPath)
        if($liveSha -cne $plan.PreviousRegistrySha256 -and $liveSha -cne $newRegistrySha){throw 'ArticleAdmissionUnknownRegistryState'}
        $published=$liveSha -ceq $newRegistrySha
        $null=Read-AARegistry $root $liveSha
    }elseif($null -ne $plan.PreviousRegistrySha256){throw 'ArticleAdmissionRegistryRemoved'}
    if($null -ne $state -and (($state.Phase -cin @('RegistryPublished','Complete') -and -not $published) -or ($state.Phase -ceq 'RolledBack' -and $published))){throw 'ArticleAdmissionTerminalRegistryChanged'}
    if($null -eq $state){
        if($published){throw 'ArticleAdmissionUnownedPublishedState'}
        if($null -eq $plan.PreviousSidecarTreeSha256){if(Test-Path -LiteralPath $root){throw 'ArticleAdmissionUnownedSidecar'}}
        elseif((Get-LABuildTreeHash $root) -cne $plan.PreviousSidecarTreeSha256){throw 'ArticleAdmissionSidecarBaselineChanged'}
    }
    if(-not $published){
        $null=Get-AAHashPin (Join-Path $profile 'data/google-long-audio/budget-ledger.json') $plan.LegacyLedgerSha256
        if((Get-LABuildTreeHash $profile) -cne $plan.ProfileTreeSha256 -or
            (Get-LABuildTreeHash (Join-Path $profile 'data/google-long-audio')) -cne $plan.RetainedEvidenceTreeSha256){throw 'ArticleAdmissionRetainedProfileChanged'}
        Assert-AAFamily $family $plan $profile
    }
    # Once the exact immutable registry was published, recovery only seals its terminal audit state.
    # Do not demand an old financial/profile snapshot or write one over newer durable reservations.
    return @{Install=$install;Profile=$profile;Root=$root;Transaction=$transaction;State=$state;Published=$published;Plan=$plan;PlanPath=$PlanPath;PlanSha=$PlanSha;PlanBytes=$planBytes;
        GrantBytes=$grantBytes;SourceBytes=$sourceBytes;NativeBytes=$nativeBytes;ReceiptBytes=$receiptBytes;ReceiptSha=$receiptSha;
        OldRegistryBytes=$oldRegistryBytes;NewRegistryBytes=$newRegistryBytes;NewRegistrySha=$newRegistrySha}
}
