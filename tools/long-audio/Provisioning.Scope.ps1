function Get-LAContext([string]$InstallRoot,[string]$ProfileRoot,[string]$ReviewedScopePath,[string]$ReviewedScopeSha256) {
    foreach($path in @($InstallRoot,$ProfileRoot,$ReviewedScopePath)){Assert-LAPhysical $path}
    Assert-LAHash $ReviewedScopeSha256
    $root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $profile=[IO.Path]::GetFullPath($ProfileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $markerPath=Join-Path $root 'cloudscribe-install.json';$markerBytes=Read-LABytes $markerPath 16384;$marker=ConvertFrom-LAJson $markerBytes
    if($marker.Product -cne 'CloudScribe Pro'){throw 'LongAudioInstallIdentity'}
    $settings=ConvertFrom-LAJson (Read-LABytes (Join-Path $root 'app/appsettings.json') 16384)
    if([IO.Path]::GetFullPath([string]$settings.CloudScribe.AppDataDirectoryOverride).TrimEnd([IO.Path]::DirectorySeparatorChar) -cne $profile){throw 'LongAudioProfileBinding'}
    $parentBytes=Read-LABytes (Join-Path $root 'chirp-article-admission-policy.json');$parentSha=Get-LAHash $parentBytes
    if($marker.ChirpArticlePolicySha256 -cne $parentSha){throw 'LongAudioParentPin'}
    $parent=ConvertFrom-LAJson $parentBytes
    if(-not (Get-Command Assert-ChirpPolicy -CommandType Function -ErrorAction SilentlyContinue)){throw 'LongAudioFullParentValidatorUnavailable'}
    $parentJson=[Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false,$true).GetString($parentBytes),[Text.Json.JsonDocumentOptions]@{MaxDepth=64})
    try{Assert-ChirpPolicy $parentJson.RootElement}finally{$parentJson.Dispose()}
    $profileSha=Get-LAHash ([Text.Encoding]::UTF8.GetBytes($profile.ToUpperInvariant()))
    $appSha=Get-LAHash (Read-LABytes (Join-Path $root 'app/CloudScribe.dll') 536870912)
    $infraSha=Get-LAHash (Read-LABytes (Join-Path $root 'app/CloudScribe.Infrastructure.dll') 536870912)
    if($parent.AppSha256 -cne $appSha -or $marker.AppSha256.ToLowerInvariant() -cne $appSha -or
        $parent.InfrastructureSha256 -cne $infraSha -or $parent.ProfileRootSha256 -cne $profileSha){throw 'LongAudioInstalledBinding'}
    $scopeBytes=Read-LABytes $ReviewedScopePath
    if((Get-LAHash $scopeBytes) -cne $ReviewedScopeSha256){throw 'LongAudioReviewedScopePin'}
    $scope=ConvertFrom-LAJson $scopeBytes
    $data=Join-Path $profile 'data/google-long-audio';$transaction=Join-Path $root 'long-audio-provisioning'
    foreach($path in @($data,$transaction)){Assert-LAPhysical $path}
    $context=@{Root=$root;Profile=$profile;MarkerPath=$markerPath;Marker=$marker;MarkerBytes=$markerBytes;Scope=$scope;ScopeBytes=$scopeBytes;
        ScopeSha=$ReviewedScopeSha256;ScopePath=(Join-Path $root 'google-long-audio-scope.json');ParentSha=$parentSha;ParentBytes=$parentBytes;
        ProfileSha=$profileSha;AppSha=$appSha;InfrastructureSha=$infraSha;Data=$data;Ledger=(Join-Path $data 'budget-ledger.json');Transaction=$transaction}
    if($scope.DocumentJobs.Count -eq $scope.ApprovedRequestCeilingsUsdCents.Count){Assert-LAScope $scope $parent $parentSha}
    else{Assert-LARetainedInstalledScope $context $parent}
    return $context
}

# Only a sealed, completed, exact installed scope may have historical flat bindings beyond current jobs.
# The temporary mapping view is never written, admitted or used to discount retained financial provision.
function Assert-LARetainedInstalledScope($Context,$Parent) {
    if(-not $Context.Marker.Contains('GoogleLongAudioScopeSha256') -or $Context.Marker.GoogleLongAudioScopeSha256 -cne $Context.ScopeSha -or
        -not (Test-Path -LiteralPath $Context.Ledger) -or -not (Test-Path -LiteralPath $Context.Transaction)){throw 'LongAudioHistoricalMappingRequiresCompletedInstallation'}
    $sealed=Read-LAPlan $Context
    if($sealed.Receipt.Phase -cne 'Complete'){throw 'LongAudioHistoricalMappingRequiresCompletedInstallation'}
    $ledger=ConvertFrom-LAJson (Read-LABytes $Context.Ledger)
    Assert-LARecord $ledger @('Version','ScopeSha256','Claims')
    if($ledger.Version -ne 1 -or $ledger.ScopeSha256 -cne $Context.ScopeSha -or $ledger.Claims -isnot [array]){throw 'LongAudioHistoricalLedgerIdentity'}
    $current=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($job in $Context.Scope.DocumentJobs){
        $source=Join-Path $Context.Profile ('documents/'+([Guid]$job.DocumentId).ToString('N')+'/content/'+([Guid]$job.RevisionId).ToString('N')+'.utf8')
        $bytes=Read-LABytes $source 1000000;if((Get-LAHash $bytes) -cne $job.SourceSha256){throw 'LongAudioSavedSourceChanged'}
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        $binding=Get-LARecoveryBinding $Context.Scope ([Guid]$job.AttemptId) $job.OutputGcsUri $text
        if(-not $current.Add($binding) -or -not $Context.Scope.ApprovedRequestCeilingsUsdCents.Contains($binding)){throw 'LongAudioHistoricalCurrentJobMapping'}
    }
    $view=ConvertFrom-LAJson $Context.ScopeBytes
    [decimal]$total=[decimal]$Parent.Budget.ProjectCeilingMinorUnits+[decimal]$Context.Scope.TaxFxProvisionUsdCents
    foreach($entry in $Context.Scope.ApprovedRequestCeilingsUsdCents.GetEnumerator()){
        Assert-LAHash $entry.Key;Assert-LAInteger $entry.Value 1;$total += [decimal]$entry.Value
        if($current.Contains($entry.Key)){continue}
        $claims=@($ledger.Claims|Where-Object {$_.RequestBindingSha256 -ceq $entry.Key})
        if($claims.Count -ne 1 -or [Guid]$claims[0].AttemptId -eq [Guid]::Empty -or $claims[0].MaximumUsdCents -ne $entry.Value -or
            ($claims[0].Contains('Recovery') -and $null -ne $claims[0].Recovery)){throw 'LongAudioHistoricalBindingNotRetained'}
        [void]$view.ApprovedRequestCeilingsUsdCents.Remove($entry.Key)
    }
    if($total -gt [decimal]$Context.Scope.TotalGoogleCapUsdCents){throw 'LongAudioCumulativeBudget'}
    Assert-LAScope $view $Parent $Context.ParentSha
    $viewContext=@{}+$Context;$viewContext.Scope=$view;Assert-LASavedInputs $viewContext
    Assert-LACompleted $Context $sealed
}
function Assert-LAScope($Scope,$Parent,[string]$ParentSha) {
    $fields=@('Version','ParentPolicySha256','SourceApprovalSha256','ProjectNumber','CredentialReferenceId','VoiceName','LanguageCode','AudioEncoding',
        'OutputBucket','TotalGoogleCapUsdCents','TaxFxProvisionUsdCents','UsdPerMillionCharacters','PricingProvenance','ApprovedRequestCeilingsUsdCents','DocumentJobs')
    if($Scope.Contains('AdaptiveRecoveryRootBindings')){$fields += 'AdaptiveRecoveryRootBindings'}
    Assert-LARecord $Scope $fields
    Assert-LAHash $Scope.SourceApprovalSha256
    if($Scope.Version -ne 1 -or $Scope.ParentPolicySha256 -cne $ParentSha -or $Parent.Currency -cne 'USD' -or $Parent.Scale -ne 2){throw 'LongAudioScopeBinding'}
    if($Scope.ProjectNumber -cnotmatch '^[0-9]{1,20}$' -or [string]::IsNullOrWhiteSpace($Scope.CredentialReferenceId) -or $Scope.CredentialReferenceId -match '\s|\p{C}') {throw 'LongAudioResourceShape'}
    foreach($identity in @($Scope.VoiceName,$Scope.LanguageCode)){if($identity -cnotmatch '^[A-Za-z0-9-]+$'){throw 'LongAudioVoiceShape'}}
    if($Scope.AudioEncoding -cnotin @('MP3','LINEAR16') -or $Scope.OutputBucket -cnotmatch '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$' -or $Scope.OutputBucket.Contains('..')){throw 'LongAudioOutputShape'}
    Assert-LAInteger $Scope.TotalGoogleCapUsdCents 1;Assert-LAInteger $Scope.TaxFxProvisionUsdCents
    if([decimal]$Scope.UsdPerMillionCharacters -lt 0 -or [string]::IsNullOrWhiteSpace($Scope.PricingProvenance)){throw 'LongAudioPricingShape'}
    Assert-LAInteger $Parent.Budget.ProjectCommittedMinorUnits;Assert-LAInteger $Parent.Budget.ProjectCeilingMinorUnits
    if($Parent.Budget.ProjectCeilingMinorUnits -lt $Parent.Budget.ProjectCommittedMinorUnits){throw 'LongAudioParentBudget'}
    if($Scope.ApprovedRequestCeilingsUsdCents -isnot [Collections.IDictionary] -or $Scope.ApprovedRequestCeilingsUsdCents.Count -eq 0){throw 'LongAudioRequestAuthorityMissing'}
    [decimal]$total=[decimal]$Parent.Budget.ProjectCeilingMinorUnits+[decimal]$Scope.TaxFxProvisionUsdCents
    foreach($entry in $Scope.ApprovedRequestCeilingsUsdCents.GetEnumerator()) {Assert-LAHash $entry.Key;Assert-LAInteger $entry.Value 1;$total += [decimal]$entry.Value}
    if($Scope.Contains('AdaptiveRecoveryRootBindings') -and $null -ne $Scope.AdaptiveRecoveryRootBindings){
        if($Scope.AdaptiveRecoveryRootBindings -isnot [array] -or $Scope.AudioEncoding -cne 'LINEAR16'){throw 'LongAudioRecoveryFamilyShape'}
        $roots=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($binding in $Scope.AdaptiveRecoveryRootBindings){
            Assert-LAHash $binding
            if(-not $roots.Add($binding) -or -not $Scope.ApprovedRequestCeilingsUsdCents.Contains($binding)){throw 'LongAudioRecoveryRootBinding'}
        }
    }
    if($total -gt [decimal]$Scope.TotalGoogleCapUsdCents){throw 'LongAudioCumulativeBudget'}
    if($Scope.DocumentJobs -isnot [array] -or $Scope.DocumentJobs.Count -eq 0){throw 'LongAudioDocumentJobsMissing'}
    if($Scope.DocumentJobs.Count -ne $Scope.ApprovedRequestCeilingsUsdCents.Count){throw 'LongAudioUnmappedRequestAuthority'}
    $documents=[Collections.Generic.HashSet[Guid]]::new();$attempts=[Collections.Generic.HashSet[Guid]]::new()
    foreach($job in $Scope.DocumentJobs){
        Assert-LARecord $job @('DocumentId','RevisionId','SourceSha256','AttemptId','OutputGcsUri','InputMode','MaximumOutputBytes','ExpectedSampleRate','ExpectedChannels')
        $document=[Guid]$job.DocumentId;$attempt=[Guid]$job.AttemptId
        if($document -eq [Guid]::Empty -or [Guid]$job.RevisionId -eq [Guid]::Empty -or $attempt -eq [Guid]::Empty -or -not $documents.Add($document) -or -not $attempts.Add($attempt)){throw 'LongAudioDocumentIdentity'}
        Assert-LAHash $job.SourceSha256
        if($job.InputMode -cne 'text'){throw 'LongAudioInputMode'}
        Assert-LAInteger $job.MaximumOutputBytes 12;Assert-LAInteger $job.ExpectedSampleRate 1;Assert-LAInteger $job.ExpectedChannels 1
        $extension=if($Scope.AudioEncoding -ceq 'MP3'){'.mp3'}else{'.wav'}
        $prefix='gs://'+$Scope.OutputBucket+'/'
        if(-not $job.OutputGcsUri.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'LongAudioBucketBinding'}
        $name=$job.OutputGcsUri.Substring($prefix.Length);$pieces=$name.Split('/')
        if($name -cnotmatch '^[A-Za-z0-9_./-]+$' -or @($pieces|Where-Object {$_ -ceq '' -or $_ -ceq '.' -or $_ -ceq '..'}).Count -gt 0 -or
            $pieces[-1] -cne ($attempt.ToString('N')+$extension)){throw 'LongAudioObjectBinding'}
    }
}
function ConvertTo-LAWire([Collections.Generic.Dictionary[string,object]]$Value) {
    return ,([Text.Json.JsonSerializer]::SerializeToUtf8Bytes($Value,$Value.GetType(),[Text.Json.JsonSerializerOptions]::new()))
}
function Assert-LASavedInputs($Context) {
    $scope=$Context.Scope;$bindings=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($job in $scope.DocumentJobs){
        $source=Join-Path $Context.Profile ('documents/'+([Guid]$job.DocumentId).ToString('N')+'/content/'+([Guid]$job.RevisionId).ToString('N')+'.utf8')
        $bytes=Read-LABytes $source 1000000
        if((Get-LAHash $bytes) -cne $job.SourceSha256){throw 'LongAudioSavedSourceChanged'}
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        if([string]::IsNullOrWhiteSpace($text)){throw 'LongAudioEmptyInput'}
        $inputValue=[Collections.Generic.Dictionary[string,object]]::new();$inputValue.Add('text',$text)
        $voice=[Collections.Generic.Dictionary[string,object]]::new();$voice.Add('languageCode',[string]$scope.LanguageCode);$voice.Add('name',[string]$scope.VoiceName)
        $audio=[Collections.Generic.Dictionary[string,object]]::new();$audio.Add('audioEncoding',[string]$scope.AudioEncoding)
        $payload=[Collections.Generic.Dictionary[string,object]]::new();$payload.Add('input',$inputValue);$payload.Add('voice',$voice);$payload.Add('audioConfig',$audio);$payload.Add('outputGcsUri',[string]$job.OutputGcsUri)
        $payloadSha=Get-LAHash (ConvertTo-LAWire $payload)
        $binding=[Collections.Generic.Dictionary[string,object]]::new()
        $binding.Add('attemptId',[Guid]$job.AttemptId);$binding.Add('projectNumber',[string]$scope.ProjectNumber);$binding.Add('location','global')
        $binding.Add('credentialReferenceId',[string]$scope.CredentialReferenceId);$binding.Add('outputGcsUri',[string]$job.OutputGcsUri);$binding.Add('PayloadSha256',$payloadSha)
        $key=Get-LAHash (ConvertTo-LAWire $binding)
        if(-not $scope.ApprovedRequestCeilingsUsdCents.Contains($key) -or -not $bindings.Add($key)){throw 'LongAudioExactWireBinding'}
        $estimate=[decimal]::Ceiling([decimal]$text.Length*[decimal]$scope.UsdPerMillionCharacters/10000)
        if($estimate -gt [decimal]$scope.ApprovedRequestCeilingsUsdCents[$key]){throw 'LongAudioEstimateExceedsHold'}
    }
}
