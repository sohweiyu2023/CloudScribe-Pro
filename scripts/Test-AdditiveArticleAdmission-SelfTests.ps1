param([switch]$FunctionsOnly)
$ErrorActionPreference='Stop'
# Inert, temporary, synthetic evidence only. This does not establish candidate-native or live admission proof.
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1','BuildRebind.Transaction.ps1')){. (Join-Path $repo ('tools/long-audio/'+$name))}
foreach($name in @('Admission.Validation.ps1','Admission.Transaction.ps1','BuildRebind.ps1')){. (Join-Path $repo ('tools/article-admission/'+$name))}
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-additive-synthetic-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot)|Out-Null
$script:caseNumber=0;$passed=0;$sha='a'*64
function Save-Synthetic([string]$Path,[byte[]]$Bytes){
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $full.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'SyntheticPathEscaped'}
    Assert-LAPhysical $full;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null;[IO.File]::WriteAllBytes($full,$Bytes)
}
function Synthetic-Article([string]$Document,[string]$Revision,[string]$Voice,[string]$TextSha,[string]$PlanSha){
    $range=[ordered]@{StartScalar=0;EndScalarExclusive=4;TextSha256=$TextSha;ReusedReceiptSha256=$null}
    $proposal=[ordered]@{DocumentId=$Document;RevisionId=$Revision;SourceSha256=$TextSha;SourceScalars=4;LanguageCode='en-US';VoiceName=$Voice;PlannerVersion='chirp3-existing-boundaries-v1';PlanSha256=$PlanSha;Parts=@([ordered]@{CoverageOrdinal=1;Range=$range;PayloadSha256=$sha;PayloadBytes=180})}
    if($Voice -ceq 'en-US-Neural2-D'){$proposal.PlannerVersion='google-neural2-compiled-boundaries-v1'}
    return [ordered]@{Proposal=$proposal;Lineage=[ordered]@{History=@([ordered]@{DocumentId=$Document;RevisionId=$Revision;StartScalar=0;EndScalarExclusive=4;TextSha256=$TextSha;Attempts=@()});Ranges=@([ordered]@{CoverageOrdinal=1;HistoricalRangeIndexes=@(0)})};ReusedAudio=@();AllowedSegments=@([ordered]@{CoverageOrdinal=1;MaximumMinorUnits=1})}
}
function New-SyntheticAdmission {
    $script:caseNumber++;$base=Join-Path $testRoot ('case-'+$script:caseNumber);$install=Join-Path $base 'install';$profile=Join-Path $base 'profile';$inputs=Join-Path $base 'reviewed'
    Save-Synthetic (Join-Path $install 'app/CloudScribe.dll') ([Text.Encoding]::UTF8.GetBytes('INERT APP'))
    Save-Synthetic (Join-Path $install 'app/CloudScribe.Infrastructure.dll') ([Text.Encoding]::UTF8.GetBytes('INERT INFRASTRUCTURE'))
    Save-Synthetic (Join-Path $install 'app/appsettings.json') (ConvertTo-LABytes @{CloudScribe=@{AppDataDirectoryOverride=$profile}})
    Save-Synthetic (Join-Path $profile '.instance.lock') ([byte[]]::new(0))
    $text=[Text.Encoding]::UTF8.GetBytes('one.');$textSha=Get-LAHash $text;$doc=[Guid]::NewGuid().ToString('D');$rev=[Guid]::NewGuid().ToString('D')
    Save-Synthetic (Join-Path $profile ('documents/'+([Guid]$doc).ToString('N')+'/content/'+([Guid]$rev).ToString('N')+'.utf8')) $text
    $article=Synthetic-Article $doc $rev 'cmn-CN-Chirp3-HD-Alnilam' $textSha $sha;$article.Proposal.LanguageCode='cmn-CN'
    $parent=[ordered]@{SchemaVersion=1;AppSha256=(Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.dll')));InfrastructureSha256=(Get-LAHash (Read-LABytes (Join-Path $install 'app/CloudScribe.Infrastructure.dll')));
        ProfileRootSha256=(Get-LAHash ([Text.Encoding]::UTF8.GetBytes($profile.ToUpperInvariant())));AccountId='synthetic-account';ProjectId='synthetic-project';Currency='USD';Scale=2;
        ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddDays(1).ToString('O');SourceApprovalSha256=$sha;Budget=[ordered]@{ProjectCommittedMinorUnits=1;ProjectCeilingMinorUnits=10;AllocationCommittedMinorUnits=1;AllocationCeilingMinorUnits=10;
        ReviewedEvidenceSha256=$sha;QueueRows=@();SpendRows=@();RecoveryRows=@();TransitionRows=@()};ScopeRows=@();PriorArticleAttemptRows=@();Articles=@($article);MaximumNewAttempts=1;MaximumAdditionalReservationMinorUnits=1}
    $parentBytes=ConvertTo-LABytes $parent;$parentSha=Get-LAHash $parentBytes
    Save-Synthetic (Join-Path $install 'chirp-article-admission-policy.json') $parentBytes
    $marker=[ordered]@{Product='CloudScribe Pro';Profile=$profile;AppSha256=$parent.AppSha256;ChirpArticlePolicySha256=$parentSha}
    Save-Synthetic (Join-Path $install 'cloudscribe-install.json') (ConvertTo-LABytes $marker)
    $attempt=[Guid]::NewGuid();$uri='gs://synthetic-bucket/'+$attempt.ToString('N')+'.mp3'
    $scope=[ordered]@{Version=1;ParentPolicySha256=$parentSha;SourceApprovalSha256=$sha;ProjectNumber='123';CredentialReferenceId='synthetic-no-secret';VoiceName='cmn-CN-Chirp3-HD-Alnilam';LanguageCode='cmn-CN';AudioEncoding='MP3';
        OutputBucket='synthetic-bucket';TotalGoogleCapUsdCents=100;TaxFxProvisionUsdCents=1;UsdPerMillionCharacters=30;PricingProvenance='SYNTHETIC';ApprovedRequestCeilingsUsdCents=@{};
        DocumentJobs=@([ordered]@{DocumentId=$doc;RevisionId=$rev;SourceSha256=$textSha;AttemptId=$attempt.ToString('D');OutputGcsUri=$uri;InputMode='text';MaximumOutputBytes=1000;ExpectedSampleRate=24000;ExpectedChannels=1})}
    $binding=Get-LARecoveryBinding $scope $attempt $uri 'one.';$scope.ApprovedRequestCeilingsUsdCents[$binding]=1
    $scopeBytes=ConvertTo-LABytes $scope;$scopePath=Join-Path $inputs 'legacy-scope.json';Save-Synthetic $scopePath $scopeBytes
    $null=Invoke-LAProvisioning $install $profile $scopePath (Get-LAHash $scopeBytes) 'Install'
    $ledgerPath=Join-Path $profile 'data/google-long-audio/budget-ledger.json'
    $ledger=ConvertFrom-LAJson (Read-LABytes $ledgerPath);$ledger.Claims=@([ordered]@{AttemptId=$attempt.ToString('D');RequestBindingSha256=$binding;MaximumUsdCents=1});Save-Synthetic $ledgerPath (ConvertTo-LABytes $ledger)
    Save-Synthetic (Join-Path $profile 'data/google-long-audio/operations/observation-pending.guard') ([Text.Encoding]::UTF8.GetBytes('SYNTHETIC RETAINED UNKNOWN; NEVER RESET'))
    $sourcePath=Join-Path $inputs 'synthetic-approval.txt';$sourceBytes=[Text.Encoding]::UTF8.GetBytes('SYNTHETIC APPROVAL ONLY; NOT LIVE USER AUTHORITY');Save-Synthetic $sourcePath $sourceBytes
    $policy=ConvertFrom-LAJson $parentBytes;$policy.SchemaVersion=3;$policy.SourceApprovalSha256=Get-LAHash $sourceBytes
    $policy.Budget.ProjectCommittedMinorUnits=10;$policy.Budget.AllocationCommittedMinorUnits=10;$policy.Budget.ProjectCeilingMinorUnits=100;$policy.Budget.AllocationCeilingMinorUnits=100
    $policy.PriorSuccessorAttemptRows=@();$policy.Articles=@();$requests=@();$i=0
    foreach($voice in @('en-US-Neural2-D','en-US-Chirp3-HD-Aoede')){
        $i++;$d=[Guid]::NewGuid().ToString('D');$r=[Guid]::NewGuid().ToString('D');$planId=$i.ToString('x64')
        Save-Synthetic (Join-Path $profile ('documents/'+([Guid]$d).ToString('N')+'/content/'+([Guid]$r).ToString('N')+'.utf8')) $text
        $a=Synthetic-Article $d $r $voice $textSha $planId;$policy.Articles+=@($a)
        $requests+=@([ordered]@{PlanSha256=$planId;CoverageOrdinal=1;Envelope=[ordered]@{AccountId='synthetic-account';CredentialReferenceId='synthetic-no-secret';CapabilityProvenanceId='synthetic-capability';PricingProvenanceId='synthetic-pricing';RequestRevision=0;VoiceName=$voice;AudioEncoding='MP3';CompiledPayloadSha256=$sha;CompiledPayloadBytes=180;RecoveryAuthorizationId=$null;ChirpAuthorizationId=$null};EstimateMinorUnits=1})
    }
    $policy.MaximumNewAttempts=2;$policy.MaximumAdditionalReservationMinorUnits=2;$policy.WholeArticleGeneration=[ordered]@{GrantId=[Guid]::NewGuid().ToString('D');PlanSha256s=@($policy.Articles|ForEach-Object {$_.Proposal.PlanSha256});MaximumCatalogRefreshes=0}
    $family=[ordered]@{FamilyId=[Guid]::NewGuid().ToString('D');Policy=$policy;AllInCeilingMinorUnits=100;ProvisionMinorUnits=10;ReviewedBaselineSha256=$sha;Requests=$requests}
    $grantPath=Join-Path $inputs 'grant.json';Save-Synthetic $grantPath (ConvertTo-LABytes $family)
    $plan=[ordered]@{SchemaVersion=1;TransactionId=[Guid]::NewGuid().ToString('D');InstallRoot=$install;ProfileRoot=$profile;ProfileRootSha256=$parent.ProfileRootSha256;AppSha256=$parent.AppSha256;InfrastructureSha256=$parent.InfrastructureSha256;
        SourceApprovalPath=$sourcePath;SourceApprovalSha256=(Get-LAHash $sourceBytes);InstallMarkerSha256=(Get-LAHash (Read-LABytes (Join-Path $install 'cloudscribe-install.json')));LegacyPolicySha256=$sha;LegacyPolicyFileSha256=$parentSha;
        LegacyScopeSha256=(Get-LAHash $scopeBytes);LegacyLedgerSha256=(Get-LAHash (Read-LABytes $ledgerPath));ProfileTreeSha256=(Get-LABuildTreeHash $profile);RetainedEvidenceTreeSha256=(Get-LABuildTreeHash (Join-Path $profile 'data/google-long-audio'));
        PreviousRegistrySha256=$null;PreviousSidecarTreeSha256=$null;GrantPath=$grantPath;GrantSha256=(Get-LAHash (Read-LABytes $grantPath));NativeProofPath=(Join-Path $inputs 'EXPLICITLY-SYNTHETIC-native-proof.json');NativeProofSha256=$sha}
    $native=[ordered]@{SchemaVersion=1;Passed=$true}
    foreach($key in @('AppSha256','InfrastructureSha256','ProfileRootSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256','LegacyScopeSha256','LegacyLedgerSha256','ProfileTreeSha256','RetainedEvidenceTreeSha256','GrantSha256','SourceApprovalSha256')){$native[$key]=$plan[$key]}
    $native.FamilyId=$family.FamilyId;$native.ReviewedBaselineSha256=$family.ReviewedBaselineSha256;$native.FamilyPolicySha256=Get-LAHash (ConvertTo-LABytes $policy);$native.AllInCeilingMinorUnits=100;$native.ProvisionMinorUnits=10;$native.ProviderCalls=0;$native.LiveWrites=0
    Save-Synthetic $plan.NativeProofPath (ConvertTo-LABytes $native);$plan.NativeProofSha256=Get-LAHash (Read-LABytes $plan.NativeProofPath)
    $path=Join-Path $inputs 'plan.json';Save-Synthetic $path (ConvertTo-LABytes $plan)
    return @{Install=$install;Profile=$profile;Plan=$plan;Path=$path;Sha=(Get-LAHash (Read-LABytes $path));Ledger=$ledgerPath}
}
function Run-Synthetic($Fixture,[string]$Phase,[scriptblock]$Stop){Invoke-AAAdmission $Fixture.Install $Fixture.Profile $Fixture.Path $Fixture.Sha $Phase $Stop}
function Expect-SyntheticRejection([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};if(-not $failed){throw 'SyntheticExpectedRejection'}}
if($FunctionsOnly){return}
foreach($checkpoint in @('Owned','PlanSealed','InputsSealed','ReceiptSealed','RegistryPublished','Complete')){
    $f=New-SyntheticAdmission;$before=Get-LABuildTreeHash $f.Profile;$marker=Read-LABytes (Join-Path $f.Install 'cloudscribe-install.json')
    $stop={param($stage) if($stage -ceq $checkpoint){throw 'SyntheticInterruption'}}.GetNewClosure()
    try{Run-Synthetic $f 'Install' $stop|Out-Null;throw 'SyntheticExpectedInterruption'}catch{if($_.Exception.Message -cne 'SyntheticInterruption'){throw}}
    Run-Synthetic $f 'Resume' $null|Out-Null
    if((Get-LABuildTreeHash $f.Profile) -cne $before -or (Get-LAHash (Read-LABytes (Join-Path $f.Install 'cloudscribe-install.json'))) -cne (Get-LAHash $marker)){throw 'SyntheticRetainedBytesChanged'}
    $state=Join-Path $f.Install ('chirp-article-admissions/transactions/'+([Guid]$f.Plan.TransactionId).ToString('N')+'/state.json');$time=(Get-Item -LiteralPath $state).LastWriteTimeUtc
    Run-Synthetic $f 'Resume' $null|Out-Null
    if((Get-Item -LiteralPath $state).LastWriteTimeUtc -ne $time){throw 'SyntheticTerminalReplayWrote'}
    Expect-SyntheticRejection {Run-Synthetic $f 'Rollback' $null};$passed++
}
foreach($checkpoint in @('Owned','PlanSealed','InputsSealed','ReceiptSealed')){
    $f=New-SyntheticAdmission;$before=Get-LABuildTreeHash $f.Profile
    $stop={param($stage) if($stage -ceq $checkpoint){throw 'SyntheticInterruption'}}.GetNewClosure()
    try{Run-Synthetic $f 'Install' $stop|Out-Null}catch{if($_.Exception.Message -cne 'SyntheticInterruption'){throw}}
    Run-Synthetic $f 'Rollback' $null|Out-Null
    if(Test-Path -LiteralPath (Join-Path $f.Install 'chirp-article-admissions/registry.json')){throw 'SyntheticRollbackAdmitted'}
    if((Get-LABuildTreeHash $f.Profile) -cne $before){throw 'SyntheticRollbackResetProfile'}
    Expect-SyntheticRejection {Run-Synthetic $f 'Resume' $null};$passed++
}
foreach($checkpoint in @('RegistryPublished','Complete')){
    $f=New-SyntheticAdmission
    $stop={param($stage) if($stage -ceq $checkpoint){throw 'SyntheticInterruption'}}.GetNewClosure()
    try{Run-Synthetic $f 'Install' $stop|Out-Null}catch{if($_.Exception.Message -cne 'SyntheticInterruption'){throw}}
    Save-Synthetic (Join-Path $f.Profile 'data/synthetic-post-publication-reservation') ([Text.Encoding]::UTF8.GetBytes('Retain this new durable reservation and uncertain outcome.'))
    $after=Get-LABuildTreeHash $f.Profile
    Run-Synthetic $f 'Resume' $null|Out-Null
    Expect-SyntheticRejection {Run-Synthetic $f 'Rollback' $null}
    if((Get-LABuildTreeHash $f.Profile) -cne $after){throw 'SyntheticPublishedResumeResetHistory'};$passed++
}
foreach($fault in @('binary','profile','grant','ledger','source-approval','native-proof','unowned-sidecar','running-profile','competing-transition','corrupt-registry','reserved-after-publication','unknown-after-publication')){
    $f=New-SyntheticAdmission;$lease=$null
    switch($fault){
        'binary' {Save-Synthetic (Join-Path $f.Install 'app/CloudScribe.dll') ([Text.Encoding]::UTF8.GetBytes('changed'))}
        'profile' {Save-Synthetic (Join-Path $f.Profile 'changed.txt') ([byte[]]@(1))}
        'grant' {Save-Synthetic $f.Plan.GrantPath ([Text.Encoding]::UTF8.GetBytes('{}'))}
        'ledger' {Save-Synthetic $f.Ledger ([Text.Encoding]::UTF8.GetBytes('{}'))}
        'source-approval' {Save-Synthetic $f.Plan.SourceApprovalPath ([byte[]]@(1))}
        'native-proof' {Save-Synthetic $f.Plan.NativeProofPath ([Text.Encoding]::UTF8.GetBytes('{}'))}
        'unowned-sidecar' {[IO.Directory]::CreateDirectory((Join-Path $f.Install 'chirp-article-admissions'))|Out-Null}
        'running-profile' {$lease=[IO.FileStream]::new((Join-Path $f.Profile '.instance.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
        'competing-transition' {Save-Synthetic (Join-Path $f.Install ('chirp-article-admissions/transactions/'+[Guid]::NewGuid().ToString('N')+'/state.json')) (ConvertTo-LABytes @{SchemaVersion=1;TransactionId=[Guid]::NewGuid().ToString('D');PlanSha256=$sha;Phase='Owned'})}
        default {
            Run-Synthetic $f 'Install' $null|Out-Null
            switch($fault){
                'corrupt-registry' {Save-Synthetic (Join-Path $f.Install 'chirp-article-admissions/registry.json') ([Text.Encoding]::UTF8.GetBytes('{}'))}
                'reserved-after-publication' {Save-Synthetic (Join-Path $f.Profile 'data/synthetic-durable-reservation') ([byte[]]@(1))}
                'unknown-after-publication' {Save-Synthetic (Join-Path $f.Profile 'data/synthetic-unknown-outcome') ([byte[]]@(1))}
            }
        }
    }
    $before=Get-LABuildTreeHash $f.Profile
    try{Expect-SyntheticRejection {Run-Synthetic $f $(if($fault -like '*publication'){'Rollback'}elseif($fault -ceq 'corrupt-registry'){'Resume'}else{'Install'}) $null}}finally{if($lease){$lease.Dispose()}}
    if((Get-LABuildTreeHash $f.Profile) -cne $before){throw 'SyntheticRejectedOperationChangedProfile'};$passed++
}
Write-Output "Additive admission synthetic self-tests passed: $passed. Fixtures retained at $testRoot. No provider calls or live installation."
