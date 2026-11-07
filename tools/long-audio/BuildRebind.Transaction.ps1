function Write-LABuildAtomic($Context,[string]$Path,[byte[]]$Bytes,[switch]$Replace) {
    Assert-LAPhysical $Path
    $stage=Join-Path $Context.History 'write-stage';Assert-LAPhysical $stage;[IO.Directory]::CreateDirectory($stage)|Out-Null
    if([IO.Path]::GetPathRoot($stage) -ine [IO.Path]::GetPathRoot($Path)){throw 'LongAudioBuildAtomicVolumeRequired'}
    $pending=Join-Path $stage ([Guid]::NewGuid().ToString('N')+'.pending');Assert-LAPhysical $pending
    try{
        $file=[IO.FileStream]::new($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$file.Write($Bytes,0,$Bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
        Assert-LAPhysical $Path;[IO.File]::Move($pending,$Path,[bool]$Replace)
    }finally{if(Test-Path -LiteralPath $pending){Assert-LAPhysical $pending;[IO.File]::Delete($pending)}}
}
function Copy-LABuildSnapshot([string]$Source,[string]$Destination,$Rows) {
    Assert-LAPhysical $Source;Assert-LAPhysical $Destination;[IO.Directory]::CreateDirectory($Destination)|Out-Null
    foreach($row in $Rows){
        $from=[IO.Path]::GetFullPath((Join-Path $Source $row.Path));$to=[IO.Path]::GetFullPath((Join-Path $Destination $row.Path))
        if(-not $from.StartsWith($Source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            -not $to.StartsWith($Destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'LongAudioBuildSnapshotEscaped'}
        Assert-LAPhysical $from;Assert-LAPhysical $to
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))|Out-Null
        if(-not (Test-Path -LiteralPath $to)){
            $stage=$Destination+'.copy-stage';Assert-LAPhysical $stage;[IO.Directory]::CreateDirectory($stage)|Out-Null
            $pending=Join-Path $stage ((Get-LAHash ([Text.Encoding]::UTF8.GetBytes($row.Path)))+'.pending');Assert-LAPhysical $pending
            if(Test-Path -LiteralPath $pending){[IO.File]::Delete($pending)} # Exact owned copy buffer outside the immutable snapshot.
            [IO.File]::Copy($from,$pending,$false)
            if((Get-FileHash -LiteralPath $pending).Hash.ToLowerInvariant() -cne $row.Sha256){throw 'LongAudioBuildCopyIncomplete'}
            [IO.File]::Move($pending,$to,$false)
        }
        if((Get-FileHash -LiteralPath $from).Hash.ToLowerInvariant() -cne $row.Sha256 -or
            (Get-FileHash -LiteralPath $to).Hash.ToLowerInvariant() -cne $row.Sha256){throw 'LongAudioBuildSnapshotChanged'}
    }
}
function Assert-LABuildProfile($Context,[switch]$OriginalOnly) {
    $rows=Get-LABuildInventory $Context.Profile
    $before=ConvertFrom-LAJson (Read-LABuildPin (Join-Path $Context.History 'profile-inventory.json') $Context.Plan.ProfileTreeSha256)
    $expected=ConvertFrom-LAJson (ConvertTo-LABytes $before)
    $ledgerRelative='data/google-long-audio/budget-ledger.json'
    foreach($row in $expected){if($row.Path -ceq $ledgerRelative){
        $actual=(Get-FileHash -LiteralPath $Context.LedgerPath).Hash.ToLowerInvariant()
        if($actual -cne $Context.Plan.PreviousLedgerSha256 -and (-not $OriginalOnly -and $actual -ceq (Get-LAHash $Context.NewLedger))){
            $row.Sha256=$actual;$row.Bytes=[long]$Context.NewLedger.Length
        }
    }}
    if((Get-LAHash (ConvertTo-LABytes $rows)) -cne (Get-LAHash (ConvertTo-LABytes $expected))){throw 'LongAudioBuildProfileChanged'}
}
function Set-LABuildPhase($Context,[string]$Phase,[scriptblock]$AfterCheckpoint) {
    $c=$Context
    $receipt=[ordered]@{Version=1;TransactionId=$Context.Plan.TransactionId;PlanSha256=$Context.PlanSha;Phase=$Phase}
    $path=Join-Path $Context.History 'receipt.json'
    Write-LABuildAtomic $c $path (ConvertTo-LABytes $receipt) -Replace:(Test-Path -LiteralPath $path)
    if($AfterCheckpoint){& $AfterCheckpoint $Phase}
}
function Invoke-LABuildRebind([string]$Install,[string]$Profile,[string]$Package,[string]$PlanPath,[string]$PlanSha,
    [ValidateSet('Inspect','Install','Resume','Rollback')][string]$Phase='Inspect',[scriptblock]$AfterCheckpoint) {
    $c=Get-LABuildRebind $Install $Profile $Package $PlanPath $PlanSha
    if($c.Sealed){
        $receipt=ConvertFrom-LAJson (Read-LABytes (Join-Path $c.History 'receipt.json') 16384)
        Assert-LARecord $receipt @('Version','TransactionId','PlanSha256','Phase')
        if($receipt.Version -ne 1 -or $receipt.TransactionId -cne $c.Plan.TransactionId -or $receipt.PlanSha256 -cne $PlanSha){throw 'LongAudioBuildReceiptChanged'}
        Assert-LABuildKnownMetadata $c
        if($receipt.Phase -ceq 'Complete' -and $Phase -cne 'Rollback'){
            Assert-LABuildFinished $c
            return [pscustomobject]@{Verified=$true;AlreadyComplete=$true;NewCommitments=0;ProviderCalls=0}
        }
        if($receipt.Phase -ceq 'RolledBack'){
            if($Phase -notin @('Inspect','Rollback')){throw 'LongAudioBuildRollbackCannotReactivate'}
            Assert-LABuildProfile $c -OriginalOnly
            Assert-LABuildRollbackFinished $c
            return [pscustomobject]@{Verified=$true;AlreadyRolledBack=$true;NewCommitments=0;ProviderCalls=0}
        }
        Assert-LABuildProfile $c
    }
    if(-not $c.Sealed){
        $old=Get-LAContext $Install $Profile (Join-Path $Install 'google-long-audio-scope.json') $c.Plan.PreviousScopeSha256
        $proof=Read-LAPlan $old;if($proof.Receipt.Phase -cne 'Complete'){throw 'LongAudioBuildIncompletePredecessor'}
        Assert-LACompleted $old $proof
        if((Get-LABuildTreeHash (Join-Path $Install 'app')) -cne $c.Plan.PreviousAppTreeSha256 -or
            (Get-LABuildTreeHash $Profile) -cne $c.Plan.ProfileTreeSha256 -or
            (Get-LABuildTreeHash (Join-Path $Install 'long-audio-provisioning')) -cne $c.Plan.PreviousProvisioningTreeSha256){throw 'LongAudioBuildBaselineChanged'}
        $operations=Join-Path $Profile 'data/google-long-audio/operations'
        if($c.Plan.Version -eq 1 -and (Test-Path -LiteralPath (Join-Path $operations 'observation-pending.guard'))){throw 'LongAudioBuildPendingProviderEvidence'}
        $retained=ConvertFrom-LAJson $c.OldLedger
        $nextScope=ConvertFrom-LAJson $c.NewScope
        if($c.Plan.Version -eq 2){
            Assert-LADeferredEvidence @{Scope=$nextScope;Profile=$Profile;Data=(Join-Path $Profile 'data/google-long-audio')} $retained
            if(@(Get-ChildItem -LiteralPath $operations -Filter '*.observation-pending.guard').Count -ne 0){throw 'LongAudioBuildAdditionalPendingProviderEvidence'}
        }
        $journals=@(Get-ChildItem -LiteralPath $operations -Filter '*.json')
        if($journals.Count -ne $retained.Claims.Count){throw 'LongAudioBuildUnmatchedOperationHistory'}
        foreach($claim in $retained.Claims){
            $path=Join-Path $operations (([Guid]$claim.AttemptId).ToString('N')+'.json')
            $state=ConvertFrom-LAJson (Read-LABytes $path 8192)
            if($c.Plan.Version -eq 2 -and @($nextScope.DeferredUnknownRanges|Where-Object {[Guid]$_.AttemptId -eq [Guid]$claim.AttemptId}).Count -eq 1){continue}
            if([Guid]$state.AttemptId -ne [Guid]$claim.AttemptId -or $state.BindingSha256 -cne $claim.RequestBindingSha256 -or
                $state.State -notin @(2,3) -or $state.OperationParseFailed -or -not $state.Observation.ReadbackVerified -or
                $state.Observation.ReadState -ne 1 -or $state.Observation.Truncated){throw 'LongAudioBuildPendingProviderEvidence'}
        }
    }
    if($Phase -ceq 'Inspect'){return [pscustomobject]@{Valid=$true;BuildOnly=$true;NewCommitments=0;ProviderCalls=0}}
    Assert-LAClosed $Install
    $instancePath=Join-Path $Profile '.instance.lock';Assert-LAPhysical $instancePath
    $instance=[IO.FileStream]::new($instancePath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
    $installLeasePath=Join-Path $Install 'long-audio-provisioning.lock';Assert-LAPhysical $installLeasePath
    $installLease=[IO.FileStream]::new($installLeasePath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
    $budgetLeasePath=(Join-Path $Profile 'data/google-long-audio/budget-ledger.json')+'.lock';Assert-LAPhysical $budgetLeasePath
    $budgetLease=[IO.FileStream]::new($budgetLeasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Invoke-LABuildRebindLocked $c $Phase $AfterCheckpoint}
    finally{$budgetLease.Dispose()}
    }finally{$installLease.Dispose()}
    }finally{$instance.Dispose()}
}
function Assert-LABuildFinished($Context) {
    $c=$Context
    $null=Read-LABuildPin (Join-Path $c.Install 'cloudscribe-install.json') (Get-LAHash $c.NewMarker)
    $null=Read-LABuildPin (Join-Path $c.Install 'chirp-article-admission-policy.json') (Get-LAHash $c.NewParent)
    $null=Read-LABuildPin (Join-Path $c.Install 'google-long-audio-scope.json') (Get-LAHash $c.NewScope)
    $null=Read-LABuildPin $c.LedgerPath (Get-LAHash $c.NewLedger)
    $candidate=Get-LABuildInventory (Join-Path $c.Package 'app')
    $oldSettings=Read-LABytes (Join-Path $c.History 'before/app/appsettings.json') 16384
    foreach($row in $candidate){if($row.Path -ceq 'appsettings.json'){$row.Sha256=Get-LAHash $oldSettings;$row.Bytes=[long]$oldSettings.Length}}
    if((Get-LABuildTreeHash (Join-Path $c.Install 'app')) -cne (Get-LAHash (ConvertTo-LABytes $candidate))){throw 'LongAudioBuildInstalledApplicationChanged'}
    $current=Get-LAContext $c.Install $c.Profile (Join-Path $c.Install 'google-long-audio-scope.json') $c.Plan.NewScopeSha256
    Assert-LACompleted $current (Read-LAPlan $current)
}
function Invoke-LABuildRebindLocked($Context,[string]$Phase,[scriptblock]$AfterCheckpoint) {
    $c=Get-LABuildRebind $Context.Install $Context.Profile $Context.Package $Context.PlanPath $Context.PlanSha
    $install=$c.Install;$before=Join-Path $c.History 'before';$app=Join-Path $install 'app';$oldApp=Join-Path $before 'app';$stage=Join-Path $c.History 'app-stage'
    foreach($path in @($before,$app,$oldApp,$stage)){Assert-LAPhysical $path;if(-not $path.StartsWith($install+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'LongAudioBuildMoveEscaped'}}
    if(-not $c.Sealed){
        if($Phase -cne 'Install'){
            $partial=ConvertFrom-LAJson (Read-LABytes (Join-Path $c.History 'receipt.json') 16384)
            if($partial.Version -ne 1 -or $partial.TransactionId -cne $c.Plan.TransactionId -or $partial.PlanSha256 -cne $c.PlanSha -or $partial.Phase -cne 'Prepared'){throw 'LongAudioBuildUseInitialInstall'}
        }
        [IO.Directory]::CreateDirectory($before)|Out-Null
        $inventory=Get-LABuildInventory $c.Profile
        if((Get-LAHash (ConvertTo-LABytes $inventory)) -cne $c.Plan.ProfileTreeSha256){throw 'LongAudioBuildProfileChanged'}
        Set-LAExactFile (Join-Path $c.History 'profile-inventory.json') (ConvertTo-LABytes $inventory)
        Copy-LABuildSnapshot $c.Profile (Join-Path $c.History 'closed-profile-backup') $inventory
        foreach($entry in @(@('cloudscribe-install.json',$c.OldMarker),@('chirp-article-admission-policy.json',$c.OldParent),@('google-long-audio-scope.json',$c.OldScope),@('budget-ledger.json',$c.OldLedger))){Set-LAExactFile (Join-Path $before $entry[0]) $entry[1]}
        Copy-LABuildSnapshot (Join-Path $install 'long-audio-provisioning') (Join-Path $before 'long-audio-provisioning') (Get-LABuildInventory (Join-Path $install 'long-audio-provisioning'))
        Set-LABuildPhase $c 'Prepared' $null
        if($AfterCheckpoint){& $AfterCheckpoint 'PreparedBeforeSeal'}
        Set-LAExactFile (Join-Path $c.History 'plan.json') $c.PlanBytes
        $c.Sealed=$true;if($AfterCheckpoint){& $AfterCheckpoint 'Prepared'}
    }elseif($Phase -ceq 'Install'){throw 'LongAudioBuildUseExactResume'}
    Assert-LABuildProfile $c
    if((Get-LABuildTreeHash (Join-Path $before 'long-audio-provisioning')) -cne $c.Plan.PreviousProvisioningTreeSha256){throw 'LongAudioBuildFrozenProvisioningChanged'}
    Assert-LABuildKnownMetadata $c
    if((Get-LABuildTreeHash (Join-Path $c.History 'closed-profile-backup')) -cne $c.Plan.ProfileTreeSha256){throw 'LongAudioBuildBackupChanged'}
    $oldSettings=Read-LABytes $(if(Test-Path -LiteralPath $oldApp){Join-Path $oldApp 'appsettings.json'}else{Join-Path $app 'appsettings.json'}) 16384
    $candidate=Get-LABuildInventory (Join-Path $c.Package 'app')
    foreach($row in $candidate){if($row.Path -ceq 'appsettings.json'){$row.Sha256=Get-LAHash $oldSettings;$row.Bytes=[long]$oldSettings.Length}}
    $candidateHash=Get-LAHash (ConvertTo-LABytes $candidate)
    if($Phase -ceq 'Rollback'){
        if(Test-Path -LiteralPath $app){
            $hash=Get-LABuildTreeHash $app
            if($hash -ceq $candidateHash){$displaced=Join-Path $c.History 'app-displaced';Assert-LAPhysical $displaced;if(Test-Path -LiteralPath $displaced){throw 'LongAudioBuildDisplacedAlreadyPresent'};[IO.Directory]::Move($app,$displaced)}
            elseif($hash -cne $c.Plan.PreviousAppTreeSha256){throw 'LongAudioBuildUnexpectedApplication'}
        }
        if(-not (Test-Path -LiteralPath $app)){if(-not (Test-Path -LiteralPath $oldApp)){throw 'LongAudioBuildOriginalApplicationMissing'};[IO.Directory]::Move($oldApp,$app)}
        foreach($entry in @(@('chirp-article-admission-policy.json',$c.OldParent),@('google-long-audio-scope.json',$c.OldScope))){Write-LABuildAtomic $c (Join-Path $install $entry[0]) $entry[1] -Replace}
        Write-LABuildAtomic $c $c.LedgerPath $c.OldLedger -Replace
        foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $before 'long-audio-provisioning') -File)){Write-LABuildAtomic $c (Join-Path $install ('long-audio-provisioning/'+$file.Name)) (Read-LABytes $file.FullName) -Replace}
        Write-LABuildAtomic $c (Join-Path $install 'cloudscribe-install.json') $c.OldMarker -Replace
        Assert-LABuildProfile $c -OriginalOnly;Assert-LABuildRollbackFinished $c;Set-LABuildPhase $c 'RolledBack' $AfterCheckpoint
        return [pscustomobject]@{RolledBack=$true;HistoryPreserved=$true;NewCommitments=0;ProviderCalls=0}
    }
    $receipt=ConvertFrom-LAJson (Read-LABytes (Join-Path $c.History 'receipt.json') 16384)
    if($receipt.Phase -ceq 'RolledBack'){throw 'LongAudioBuildRollbackCannotReactivate'}
    $pending=ConvertFrom-LAJson $c.NewMarker;$pending.LongAudioBuildRebindPending=$true
    Write-LABuildAtomic $c (Join-Path $install 'cloudscribe-install.json') (ConvertTo-LABytes $pending) -Replace
    if(-not (Test-Path -LiteralPath $oldApp)){
        if((Get-LABuildTreeHash $app) -cne $c.Plan.PreviousAppTreeSha256){throw 'LongAudioBuildOriginalApplicationChanged'}
        [IO.Directory]::Move($app,$oldApp);Set-LABuildPhase $c 'OldAppArchived' $AfterCheckpoint
    }
    if(-not (Test-Path -LiteralPath $app)){
        $packageRows=Get-LABuildInventory (Join-Path $c.Package 'app')
        $copyRows=@($packageRows|Where-Object {$_.Path -cne 'appsettings.json'})
        Copy-LABuildSnapshot (Join-Path $c.Package 'app') $stage $copyRows
        $settingsPath=Join-Path $stage 'appsettings.json'
        if((Test-Path -LiteralPath $settingsPath) -and (Get-LAHash (Read-LABytes $settingsPath 16384)) -cne (Get-LAHash $oldSettings)){throw 'LongAudioBuildStageSettingsChanged'}
        Write-LABuildAtomic $c (Join-Path $stage 'appsettings.json') $oldSettings -Replace
        if((Get-LABuildTreeHash $stage) -cne $candidateHash){throw 'LongAudioBuildStageChanged'}
        [IO.Directory]::Move($stage,$app);Set-LABuildPhase $c 'AppPublished' $AfterCheckpoint
    }
    if((Get-LABuildTreeHash $app) -cne $candidateHash -or (Get-LABuildTreeHash $oldApp) -cne $c.Plan.PreviousAppTreeSha256){throw 'LongAudioBuildApplicationChanged'}
    foreach($entry in @(@('chirp-article-admission-policy.json',$c.NewParent,'ParentWritten'),@('google-long-audio-scope.json',$c.NewScope,'ScopeWritten'))){Write-LABuildAtomic $c (Join-Path $install $entry[0]) $entry[1] -Replace;Set-LABuildPhase $c $entry[2] $AfterCheckpoint}
    Write-LABuildAtomic $c $c.LedgerPath $c.NewLedger -Replace;Set-LABuildPhase $c 'LedgerWritten' $AfterCheckpoint
    Complete-LABuildProof $c
    Assert-LABuildProfile $c
    Write-LABuildAtomic $c (Join-Path $install 'cloudscribe-install.json') $c.NewMarker -Replace;Set-LABuildPhase $c 'MarkerPublished' $AfterCheckpoint
    $current=Get-LAContext $install $c.Profile (Join-Path $install 'google-long-audio-scope.json') $c.Plan.NewScopeSha256
    Assert-LACompleted $current (Read-LAPlan $current)
    Set-LABuildPhase $c 'Complete' $AfterCheckpoint
    return [pscustomobject]@{Installed=$true;BuildOnly=$true;Executable=(Join-Path $app 'CloudScribe.exe');PreservedClaimCount=(ConvertFrom-LAJson $c.NewLedger).Claims.Count;NewCommitments=0;ProviderCalls=0;History=$c.History}
}
function Assert-LABuildRollbackFinished($Context) {
    $c=$Context
    foreach($entry in @(@('cloudscribe-install.json',$c.Plan.PreviousMarkerSha256),@('chirp-article-admission-policy.json',$c.Plan.PreviousParentSha256),@('google-long-audio-scope.json',$c.Plan.PreviousScopeSha256))){
        $null=Read-LABuildPin (Join-Path $c.Install $entry[0]) $entry[1]
    }
    $null=Read-LABuildPin $c.LedgerPath $c.Plan.PreviousLedgerSha256
    if((Get-LABuildTreeHash (Join-Path $c.Install 'app')) -cne $c.Plan.PreviousAppTreeSha256 -or
        (Get-LABuildTreeHash (Join-Path $c.Install 'long-audio-provisioning')) -cne $c.Plan.PreviousProvisioningTreeSha256){throw 'LongAudioBuildRollbackReadback'}
    $previous=Get-LAContext $c.Install $c.Profile (Join-Path $c.Install 'google-long-audio-scope.json') $c.Plan.PreviousScopeSha256
    Assert-LACompleted $previous (Read-LAPlan $previous)
}
function Assert-LABuildKnownMetadata($Context) {
    $c=$Context;$pending=ConvertFrom-LAJson $c.NewMarker;$pending.LongAudioBuildRebindPending=$true
    foreach($entry in @(@('cloudscribe-install.json',@((Get-LAHash $c.OldMarker),(Get-LAHash $c.NewMarker),(Get-LAHash (ConvertTo-LABytes $pending)))),
        @('chirp-article-admission-policy.json',@((Get-LAHash $c.OldParent),(Get-LAHash $c.NewParent))),
        @('google-long-audio-scope.json',@((Get-LAHash $c.OldScope),(Get-LAHash $c.NewScope))))){
        if((Get-LAHash (Read-LABytes (Join-Path $c.Install $entry[0]))) -cnotin $entry[1]){throw 'LongAudioBuildUnknownIntermediateMetadata'}
    }
    if((Get-LAHash (Read-LABytes $c.LedgerPath)) -cnotin @((Get-LAHash $c.OldLedger),(Get-LAHash $c.NewLedger))){throw 'LongAudioBuildUnknownIntermediateLedger'}
    $frozen=Join-Path $c.History 'before/long-audio-provisioning'
    if(Test-Path -LiteralPath $frozen){
        $oldRows=Get-LABuildInventory $frozen;$newRows=@{}
        if(Test-Path -LiteralPath (Join-Path $c.History 'new-provision-plan.json')){
            foreach($entry in (Get-LABuildProofFiles $c)){$newRows[$entry[0]]=Get-LAHash $entry[1]}
        }
        $liveRows=Get-LABuildInventory (Join-Path $c.Install 'long-audio-provisioning')
        if($liveRows.Count -ne $oldRows.Count){throw 'LongAudioBuildUnknownIntermediateProof'}
        foreach($row in $liveRows){
            $previous=@($oldRows|Where-Object {$_.Path -ceq $row.Path})
            if($previous.Count -ne 1 -or ($row.Sha256 -cne $previous[0].Sha256 -and
                (-not $newRows.ContainsKey($row.Path) -or $row.Sha256 -cne $newRows[$row.Path]))){throw 'LongAudioBuildUnknownIntermediateProof'}
        }
    }
}
function Get-LABuildProofFiles($Context) {
    $c=$Context;$path=Join-Path $c.History 'new-provision-plan.json'
    $bytes=Read-LABytes $path 16384;$plan=ConvertFrom-LAJson $bytes
    Assert-LARecord $plan @('Version','TransactionId','ScopeSha256','ParentPolicySha256','ProfileRootSha256','AppSha256','InfrastructureSha256','PreviousMarkerSha256','NewMarkerSha256','InitialLedgerSha256','CreatedUtc')
    $raw=[Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false,$true).GetString($bytes))
    try{$created=$raw.RootElement.GetProperty('CreatedUtc').GetDateTimeOffset()}finally{$raw.Dispose()}
    if($plan.Version -ne 1 -or $plan.TransactionId -cne $c.Plan.TransactionId -or
        $plan.ProfileRootSha256 -cne $c.Parent.ProfileRootSha256 -or $plan.AppSha256 -cne $c.Parent.AppSha256 -or
        $plan.InfrastructureSha256 -cne $c.Parent.InfrastructureSha256 -or $plan.PreviousMarkerSha256 -cne $c.Plan.PreviousMarkerSha256 -or
        $created.Offset -ne [TimeSpan]::Zero -or
        $plan.ScopeSha256 -cne $c.Plan.NewScopeSha256 -or $plan.ParentPolicySha256 -cne $c.Plan.NewParentSha256 -or
        $plan.NewMarkerSha256 -cne (Get-LAHash $c.NewMarker) -or $plan.InitialLedgerSha256 -cne (Get-LAHash $c.NewLedger)){throw 'LongAudioBuildSealedProofChanged'}
    $receipt=[ordered]@{Version=1;TransactionId=$plan.TransactionId;PlanSha256=(Get-LAHash $bytes);Phase='Complete'}
    return ,@(@('plan.json',$bytes),@('scope.json',$c.NewScope),@('ledger-initial.json',$c.NewLedger),@('marker-before.json',$c.OldMarker),@('marker-after.json',$c.NewMarker),@('receipt.json',(ConvertTo-LABytes $receipt)))
}
function Complete-LABuildProof($Context) {
    $c=$Context;$path=Join-Path $c.History 'new-provision-plan.json'
    if(-not (Test-Path -LiteralPath $path)){
        $plan=[ordered]@{Version=1;TransactionId=$c.Plan.TransactionId;ScopeSha256=$c.Plan.NewScopeSha256;ParentPolicySha256=$c.Plan.NewParentSha256;
            ProfileRootSha256=$c.Parent.ProfileRootSha256;AppSha256=$c.Parent.AppSha256;InfrastructureSha256=$c.Parent.InfrastructureSha256;
            PreviousMarkerSha256=$c.Plan.PreviousMarkerSha256;NewMarkerSha256=(Get-LAHash $c.NewMarker);InitialLedgerSha256=(Get-LAHash $c.NewLedger);CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
        $bytes=ConvertTo-LABytes $plan;Set-LAExactFile $path $bytes
    }
    $entries=Get-LABuildProofFiles $c
    Assert-LABuildKnownMetadata $c
    $root=Join-Path $c.Install 'long-audio-provisioning'
    foreach($entry in $entries){Write-LABuildAtomic $c (Join-Path $root $entry[0]) $entry[1] -Replace}
}
