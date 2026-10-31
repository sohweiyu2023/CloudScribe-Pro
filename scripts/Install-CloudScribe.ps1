[CmdletBinding()]
param(
    [string]$PackageRoot=$PSScriptRoot,
    [string]$MigrateProfileFrom,
    [string]$ReviewedRecoveryPolicyPath,
    [string]$ReviewedRecoveryPolicySha256,
    [string]$ReviewedChirpArticlePolicyPath,
    [string]$ReviewedChirpArticlePolicySha256,
    [string]$ReviewedChirpResumePolicyPath,
    [string]$ReviewedChirpResumePolicySha256,
    [string]$ReviewedCompletionGatePath,
    [string]$ReviewedCompletionGateSha256,
    [string]$ReviewedCompletionBuildTransitionPath,
    [string]$ReviewedCompletionBuildTransitionSha256,
    [string]$ReviewedCompletionSuccessorRunPath,
    [string]$ReviewedCompletionSuccessorRunSha256,
    [switch]$CompletePendingCompletionSuccessor,
    [switch]$ValidateOnly,
    [switch]$ValidatePackageOnly
)
$ErrorActionPreference='Stop'
if([bool]$ReviewedCompletionSuccessorRunPath -ne [bool]$ReviewedCompletionSuccessorRunSha256){throw 'A successor run requires independently reviewed handoff bytes and hash.'}
if($ReviewedCompletionSuccessorRunPath -and (-not $ReviewedCompletionGatePath -or -not $ReviewedCompletionGateSha256 -or $ReviewedCompletionBuildTransitionPath -or $MigrateProfileFrom -or $ReviewedRecoveryPolicyPath -or $ReviewedChirpArticlePolicyPath -or $ReviewedChirpResumePolicyPath)){throw 'A successor run requires its new gate and existing profile, separately from all financial admissions and build transitions.'}
if($ValidatePackageOnly -and ($ReviewedCompletionSuccessorRunPath -or $ReviewedCompletionSuccessorRunSha256 -or $CompletePendingCompletionSuccessor)){throw 'A private successor run is never a clean package input.'}
if($CompletePendingCompletionSuccessor -and (-not $ReviewedCompletionSuccessorRunPath -or $ValidateOnly)){throw 'Pending successor completion requires the same reviewed handoff and actual installed readback; it is not a new install or preflight.'}
if([bool]$ReviewedCompletionBuildTransitionPath -ne [bool]$ReviewedCompletionBuildTransitionSha256){throw 'A completion build succession requires independently reviewed transition bytes and hash.'}
if($ReviewedCompletionBuildTransitionPath -and (-not $ReviewedCompletionGatePath -or -not $ReviewedCompletionGateSha256 -or $MigrateProfileFrom -or $ReviewedRecoveryPolicyPath -or $ReviewedChirpArticlePolicyPath -or $ReviewedChirpResumePolicyPath)){throw 'A build succession requires its explicit new gate and existing profile, separately from every financial/policy admission.'}
if($ValidatePackageOnly -and ($ReviewedCompletionBuildTransitionPath -or $ReviewedCompletionBuildTransitionSha256)){throw 'A private build transition is never a clean package input.'}
if([bool]$ReviewedCompletionGatePath -ne [bool]$ReviewedCompletionGateSha256){throw 'Completion gate admission requires separately reviewed bytes and hash.'}
if($ValidatePackageOnly -and ($ReviewedCompletionGatePath -or $ReviewedCompletionGateSha256)){throw 'Private completion gate is excluded from clean packages.'}
if($ValidatePackageOnly -and ($ValidateOnly -or $MigrateProfileFrom)){throw 'ValidatePackageOnly cannot be combined with installation or migration preflight.'}
if($ValidatePackageOnly -and ($ReviewedRecoveryPolicyPath -or $ReviewedRecoveryPolicySha256)){throw 'A private recovery policy is not part of a clean package.'}
if([bool]$ReviewedRecoveryPolicyPath -ne [bool]$ReviewedRecoveryPolicySha256){throw 'Recovery policy admission requires both the separately reviewed file and its independently approved SHA256.'}
if($ValidatePackageOnly -and ($ReviewedChirpArticlePolicyPath -or $ReviewedChirpArticlePolicySha256)){throw 'A private article policy is not part of a clean package.'}
if([bool]$ReviewedChirpArticlePolicyPath -ne [bool]$ReviewedChirpArticlePolicySha256){throw 'Article policy admission requires its separate reviewed file and independently approved SHA256.'}
if($ReviewedRecoveryPolicyPath -and $ReviewedChirpArticlePolicyPath){throw 'Admit one paid recovery/article authority at a time; historical output import is a separate local operation.'}
if($ValidatePackageOnly -and ($ReviewedChirpResumePolicyPath -or $ReviewedChirpResumePolicySha256)){throw 'A private reservation resume policy is not part of a clean package.'}
if([bool]$ReviewedChirpResumePolicyPath -ne [bool]$ReviewedChirpResumePolicySha256){throw 'Resume admission requires its separately reviewed file and independently approved SHA256.'}
if($ReviewedChirpResumePolicyPath -and ($ReviewedRecoveryPolicyPath -or $ReviewedChirpArticlePolicyPath)){throw 'Resume authority cannot be combined with a new-reservation authority.'}
if($ReviewedChirpResumePolicyPath -and $MigrateProfileFrom){throw 'A retained reservation must stay in its reviewed existing profile; migration needs a separate reviewed transition.'}
$package=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PackageRoot).Path)
if($package.Length -gt [IO.Path]::GetPathRoot($package).Length){$package=$package.TrimEnd([char[]]@('\','/'))}
$packagePrefix=$package.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar
function Assert-NoLinks([string]$Path){
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked install/profile path is not allowed: $current"}
        $current=Split-Path -Parent $current
    }
}

function Invoke-CompletionGate($Gate,[string]$Command,[Guid]$OperationId,[string]$Identity,$Receipt,[string]$ApplicationDirectory,[string]$PolicyPath){
    foreach($entry in @(@($Gate.Config.PythonPath,$Gate.Config.PythonSha256),@($Gate.Config.ScriptPath,$Gate.Config.ScriptSha256),@((Join-Path (Split-Path -Parent $Gate.Config.ScriptPath) 'completion_register.py'),$Gate.Config.ModuleSha256))){
        Assert-NoLinks $entry[0]
        if((Get-FileHash -LiteralPath $entry[0] -Algorithm SHA256).Hash -ine $entry[1]){throw 'Completion bridge material differs from its reviewed hash.'}
    }
    $start=[Diagnostics.ProcessStartInfo]::new($Gate.Config.PythonPath)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add('-I');$start.ArgumentList.Add('-S');$start.ArgumentList.Add($Gate.Config.ScriptPath)
    $process=[Diagnostics.Process]::Start($start)
    if(-not $process){throw 'Completion bridge did not start.'}
    try{
        $inputRecord=@{command=$Command;configPath=$Gate.Path;configSha256=$Gate.Sha256;appDirectory=$ApplicationDirectory;id=$OperationId.ToString('D');identitySha256=$Identity;receipt=$Receipt;policyPath=$PolicyPath}
        if($Command -in @('inspect-build','claim-build','check-build','complete-build')){
            if(-not $Gate.BuildTransition){throw 'An exact independently reviewed build transition is required.'}
            $inputRecord.transitionPath=$Gate.BuildTransition.Path
            $inputRecord.transitionSha256=$Gate.BuildTransition.Sha256
        }
        if($Command -in @('inspect-successor','claim-successor','check-successor','complete-successor')){
            if(-not $Gate.SuccessorTransition){throw 'An exact independently reviewed successor-run handoff is required.'}
            $inputRecord.handoffPath=$Gate.SuccessorTransition.Path
            $inputRecord.handoffSha256=$Gate.SuccessorTransition.Sha256
        }
        $process.StandardInput.Write(($inputRecord|ConvertTo-Json -Depth 8 -Compress));$process.StandardInput.Close()
        $outputTask=$process.StandardOutput.ReadToEndAsync();$errorTask=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(15000)){throw 'Completion bridge timeout: outcome requires reconciliation; do not retry.'}
        $output=$outputTask.GetAwaiter().GetResult();$errorText=$errorTask.GetAwaiter().GetResult()
        if($output.Length -gt 8192 -or $errorText.Length -gt 8192){throw 'Completion bridge protocol bound failed; reconcile the private register.'}
        $responseJson=[Text.Json.JsonDocument]::Parse($output)
        try{Assert-UniqueRecoveryJson $responseJson.RootElement}finally{$responseJson.Dispose()}
        $result=$output|ConvertFrom-Json
        $safeReason=if($result.PSObject.Properties.Name -contains 'reason' -and $result.reason -cin @('Unknown','Expired','ClockRollback','ClockDiscontinuity','PendingOutcome','Limit','PinMismatch','Storage','Protocol','Stopped','SuccessorHeadMismatch','SuccessorDatabaseMismatch','SuccessorScopeMismatch','SuccessorPending','PilotEvidenceMissing','PilotEvidenceMismatch','SuccessorNotActivated','RunRetired')){$result.reason}else{'Unknown'}
        $safeCode=if($result.PSObject.Properties.Name -contains 'code' -and $result.code -cin @('SuccessorHeadMismatch','TerminalDatabasePin','TerminalDatabaseChanged','SuccessorNotActivated','PredecessorNotRetired','RunRetired','OutsideWindow','PilotNotAccepted','PilotAttempt','PilotOutputIncomplete','PilotStoredPlan','PilotPolicyPin','PilotAttemptIdentity','PilotOutputPin','PilotAcceptedIdentity','PilotMediaLocation','PilotMediaPin','PilotMappingAbsent','PilotMappingBound','PilotMappingChain','PilotMappingIdentity','PilotMappingDispatch','PilotMappingReceipt','ClosedDatabaseRequired','TerminalAttempt','TerminalQueue','TerminalFailureEvidence','AcceptedWorkCannotRetire','RecoverableOutputCannotRetire','PendingOrUncertainWork','RetirementIdentityMismatch','SuccessorAlreadyExists','SuccessorAlreadyActivated','HandoffPin','HandoffConfigPin','HandoffScopeConfig','HandoffNewScopeBinding','HandoffApplicationLocation','SuccessorScopeBinding','SuccessorActivationReplay')){$result.code}else{'Unclassified'}
        if($process.ExitCode -ne 0 -or $result.PSObject.Properties.Name -contains 'rejected' -or ($Command -in @('complete','complete-build','complete-successor') -and $result.outcome -cne 'Succeeded')){throw "Completion operation stopped: $safeReason [$safeCode]. Reconcile its private register; do not retry automatically."}
        if($Command -in @('claim-build','claim-successor') -and ($result.id -cne $OperationId.ToString('D') -or $result.identitySha256 -cne $Identity -or $result.scopeSha256 -cne $Gate.Config.ScopeSha256)){throw 'The transition claim response is not bound to this exact operation.'}
        if($Command -in @('inspect-build','check-build','inspect-successor','check-successor') -and $result.valid -ne $true){throw 'Transition readiness was not positively verified.'}
        return $result
    }finally{$process.Dispose()}
}

function Get-ReviewedCompletionSuccessorRun([string]$GatePath,[string]$GatePin,$Config,[string]$OldGatePin){
    if(-not $ReviewedCompletionSuccessorRunPath){return $null}
    $handoffPath=(Resolve-Path -LiteralPath $ReviewedCompletionSuccessorRunPath).Path;Assert-NoLinks $handoffPath
    if($ReviewedCompletionSuccessorRunSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-Item -LiteralPath $handoffPath).Length -gt 32768 -or
        (Get-FileHash -LiteralPath $handoffPath).Hash.ToLowerInvariant() -cne $ReviewedCompletionSuccessorRunSha256){throw 'The successor-run handoff differs from its independently reviewed hash.'}
    $json=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($handoffPath)))
    try{
        Assert-UniqueRecoveryJson $json.RootElement
        Assert-RecoveryRecord $json.RootElement @{SchemaVersion='Int32';TransitionId='NonemptyGuid';SourceApprovalSha256='String';PreviousConfigPath='String';PreviousGateSha256='String';PreviousScopeSha256='String';ExpectedPreviousHeadSha256='String';NewConfigPath='String';NewGateSha256='String';NewScopePath='String';NewScopeSha256='String';DatabaseSha256='String';FailedAuthorizationId='NonemptyGuid';FailedLocalAttemptId='String';FailedPlanSha256='String';FailedCoverageOrdinal='Int32';FailedPayloadSha256='String';FailedPayloadBytes='Int32'}
    }finally{$json.Dispose()}
    $record=[IO.File]::ReadAllText($handoffPath)|ConvertFrom-Json
    if($record.SchemaVersion -ne 1 -or $record.NewGateSha256 -cne $GatePin -or $record.NewScopeSha256 -cne $Config.ScopeSha256 -or
        [IO.Path]::GetFullPath($record.NewConfigPath) -ine [IO.Path]::GetFullPath($GatePath) -or
        ($record.PreviousGateSha256 -cne $OldGatePin -and (-not $CompletePendingCompletionSuccessor -or $GatePin -cne $OldGatePin))){throw 'The successor handoff does not bind the observed predecessor and reviewed successor.'}
    foreach($field in @('SourceApprovalSha256','PreviousGateSha256','PreviousScopeSha256','ExpectedPreviousHeadSha256','NewGateSha256','NewScopeSha256','DatabaseSha256','FailedPlanSha256','FailedPayloadSha256')){
        if($record.$field -cnotmatch '^[a-f0-9]{64}$'){throw 'The successor handoff has malformed identity pins.'}
    }
    return [pscustomobject]@{Path=$handoffPath;Sha256=$ReviewedCompletionSuccessorRunSha256;Id=[Guid]$record.TransitionId;PreviousGateSha256=$record.PreviousGateSha256;PreviousScopeSha256=$record.PreviousScopeSha256;NewScopePath=$record.NewScopePath}
}

function Get-ReviewedCompletionGate([string]$Profile){
    $path=$ReviewedCompletionGatePath;$pin=$ReviewedCompletionGateSha256
    $requiredPath=if($Profile){Join-Path $Profile '.completion-workflow-required.json'}else{$null}
    $oldGatePin=if($identity -and $identity.PSObject.Properties.Name -contains 'CompletionWorkflowGateSha256'){$identity.CompletionWorkflowGateSha256}else{$null}
    if(-not $path -and $oldGatePin){
        if(-not $ReviewedChirpResumePolicyPath -and -not $ReviewedChirpArticlePolicyPath){throw 'An active completion gate cannot be silently dropped by an ordinary update.'}
        $path=Join-Path $target 'completion-workflow-gate.json';$pin=$oldGatePin
    }
    if(-not $path){
        if($requiredPath -and (Test-Path -LiteralPath $requiredPath)){throw 'Partial completion gate activation requires explicit reconciliation.'}
        return $null
    }
    if(-not $Profile -or $MigrateProfileFrom -or $pin -cnotmatch '^[a-f0-9]{64}$'){throw 'Completion gate requires its exact existing profile and reviewed hash.'}
    if(-not $identity -or $identity.PSObject.Properties.Name -notcontains 'CompletionGateProtocolVersion' -or $identity.CompletionGateProtocolVersion -ne 1){throw 'Install and verify the gate-capable build first, without activating a live scope.'}
    $path=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $path).Path);Assert-NoLinks $path
    if((Get-Item -LiteralPath $path).Length -gt 32768){throw 'Completion gate exceeds its bound.'}
    $bytes=[IO.File]::ReadAllBytes($path)
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $pin){throw 'Completion gate pin differs.'}
    $json=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($bytes))
    try{Assert-UniqueRecoveryJson $json.RootElement}finally{$json.Dispose()}
    $config=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
    $transition=$null
    if($ReviewedCompletionBuildTransitionPath){
        $transitionPath=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ReviewedCompletionBuildTransitionPath).Path);Assert-NoLinks $transitionPath
        if($ReviewedCompletionBuildTransitionSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-Item -LiteralPath $transitionPath).Length -gt 32768 -or
            (Get-FileHash -LiteralPath $transitionPath -Algorithm SHA256).Hash -ine $ReviewedCompletionBuildTransitionSha256){throw 'The private build transition differs from its independently reviewed hash.'}
        $transitionBytes=[IO.File]::ReadAllBytes($transitionPath)
        $transitionJson=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($transitionBytes))
        try{Assert-UniqueRecoveryJson $transitionJson.RootElement}finally{$transitionJson.Dispose()}
        $record=[Text.Encoding]::UTF8.GetString($transitionBytes)|ConvertFrom-Json
        if($record.SchemaVersion -ne 1 -or $record.NewGateSha256 -cne $pin -or $record.PreviousGateSha256 -cne $oldGatePin -or $record.ScopeSha256 -cne $config.ScopeSha256 -or [Guid]$record.TransitionId -eq [Guid]::Empty){throw 'The build transition does not name the exact predecessor, successor and unchanged scope.'}
        $transition=[pscustomobject]@{Path=$transitionPath;Sha256=$ReviewedCompletionBuildTransitionSha256;Id=[Guid]$record.TransitionId;PreviousGateSha256=$record.PreviousGateSha256}
    }
    $successor=Get-ReviewedCompletionSuccessorRun $path $pin $config $oldGatePin
    $profileSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()))).ToLowerInvariant()
    if($config.SchemaVersion -ne 1 -or $config.ProfileRootSha256 -cne $profileSha -or
        [IO.Path]::GetFullPath($config.ApplicationDirectory).TrimEnd([char[]]@('\','/')) -ine (Join-Path $target 'app') -or
        [IO.Path]::GetFullPath($config.CandidateApplicationDirectory).TrimEnd([char[]]@('\','/')) -ine (Join-Path $package 'app') -or
        (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash -ine $config.AppSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash -ine $config.InfrastructureSha256){throw 'Completion gate build/profile/package binding differs.'}
    if(-not $transition -and -not $successor -and ((Get-FileHash -LiteralPath (Join-Path $target 'app/CloudScribe.dll')).Hash -ine $config.AppSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $target 'app/CloudScribe.Infrastructure.dll')).Hash -ine $config.InfrastructureSha256)){throw 'Activate completion only on the already verified exact installed hook build.'}
    if($oldGatePin -and $oldGatePin -cne $pin -and -not $transition -and -not $successor){throw 'A completion run cannot be reset or replaced implicitly.'}
    if($requiredPath -and (Test-Path -LiteralPath $requiredPath)){
        Assert-NoLinks $requiredPath
        $required=Get-Content -LiteralPath $requiredPath -Raw|ConvertFrom-Json
        $expectedRequiredPin=if($transition){$transition.PreviousGateSha256}elseif($successor){$successor.PreviousGateSha256}else{$pin}
        $expectedRequiredScope=if($successor){$successor.PreviousScopeSha256}else{$config.ScopeSha256}
        $alreadySwitched=$CompletePendingCompletionSuccessor -and $successor -and $required.GateSha256 -ceq $pin -and $required.ScopeSha256 -ceq $config.ScopeSha256
        if(-not $alreadySwitched -and ($required.GateSha256 -cne $expectedRequiredPin -or $required.ScopeSha256 -cne $expectedRequiredScope)){throw 'Completion requirement differs; no reset permitted.'}
    }elseif($transition -or $successor){
        throw 'A reviewed succession requires the existing persistent required marker.'
    }
    $gate=[pscustomobject]@{Path=$path;Sha256=$pin;Bytes=$bytes;Config=$config;RequiredPath=$requiredPath;BuildTransition=$transition;SuccessorTransition=$successor}
    $inspection=if($CompletePendingCompletionSuccessor){
        Assert-NoLinks $successor.NewScopePath
        if((Get-Item -LiteralPath $successor.NewScopePath).Length -gt 32768){throw 'The reviewed successor scope exceeds its bound.'}
        $pendingScope=[IO.File]::ReadAllText($successor.NewScopePath)|ConvertFrom-Json
        [pscustomobject]@{deadlineUtc=$pendingScope.endUtc}
    }elseif($successor){Invoke-CompletionGate $gate 'inspect-successor' $successor.Id $successor.Sha256 $null (Join-Path $package 'app')}elseif($transition){Invoke-CompletionGate $gate 'inspect-build' $transition.Id $transition.Sha256 $null (Join-Path $package 'app')}else{Invoke-CompletionGate $gate 'inspect' ([Guid]::Empty) '' $null (Join-Path $package 'app')}
    $gate|Add-Member -NotePropertyName DeadlineUtc -NotePropertyValue ([DateTimeOffset]$inspection.deadlineUtc)
    return $gate
}

function Assert-CompletionInstallEffect($Gate,$OperationId,$OperationIdentity,$Resume){
    if(-not $Gate){return}
    $null=Invoke-CompletionGate $Gate 'check' $OperationId $OperationIdentity $null (Join-Path $package 'app')
    if($Resume){
        $policy=[Text.Encoding]::UTF8.GetString($Resume.Bytes)|ConvertFrom-Json
        if([DateTimeOffset]$policy.ExpiresAtUtc -le [DateTimeOffset]::UtcNow -or [DateTimeOffset]$policy.CatalogExpiresAtUtc -le [DateTimeOffset]::UtcNow){throw 'Resume admission expired before installer effect; retain its pending register state.'}
    }
}

function Complete-CompletionBuildInstall($Gate,[string]$InstalledApp){
    if(-not $Gate -or (-not $Gate.BuildTransition -and -not $Gate.SuccessorTransition)){throw 'A reviewed pending succession is required.'}
    $successor=[bool]$Gate.SuccessorTransition
    $transition=if($successor){$Gate.SuccessorTransition}else{$Gate.BuildTransition}
    if([IO.Path]::GetFullPath($InstalledApp).TrimEnd([char[]]@('\','/')) -ine [IO.Path]::GetFullPath($Gate.Config.ApplicationDirectory).TrimEnd([char[]]@('\','/'))){throw 'The successor readback directory changed.'}
    Assert-NoLinks $InstalledApp
    foreach($pair in @(@('CloudScribe.dll',$Gate.Config.AppSha256),@('CloudScribe.Infrastructure.dll',$Gate.Config.InfrastructureSha256))){
        if((Get-FileHash -LiteralPath (Join-Path $InstalledApp $pair[0]) -Algorithm SHA256).Hash -ine $pair[1]){throw 'Installed successor differs; preserve pending state.'}
    }
    $installRoot=Split-Path -Parent $InstalledApp
    $marker=Get-Content -LiteralPath (Join-Path $installRoot 'cloudscribe-install.json') -Raw|ConvertFrom-Json
    if($marker.Product -cne 'CloudScribe Pro' -or $marker.CompletionGateProtocolVersion -ne 1 -or $marker.CompletionWorkflowGateSha256 -cne $Gate.Sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $installRoot 'completion-workflow-gate.json') -Algorithm SHA256).Hash -ine $Gate.Sha256){throw 'The installed successor pin differs; preserve pending state.'}
    # Pending journal state blocks both clients throughout the cross-file switch.
    Assert-NoLinks $Gate.RequiredPath
    $prior=Get-Content -LiteralPath $Gate.RequiredPath -Raw|ConvertFrom-Json
    $oldScope=if($successor){$transition.PreviousScopeSha256}else{$Gate.Config.ScopeSha256}
    $alreadySwitched=$successor -and $CompletePendingCompletionSuccessor -and $prior.GateSha256 -ceq $Gate.Sha256 -and $prior.ScopeSha256 -ceq $Gate.Config.ScopeSha256
    if(-not $alreadySwitched -and ($prior.GateSha256 -cne $transition.PreviousGateSha256 -or $prior.ScopeSha256 -cne $oldScope)){throw 'The predecessor requirement changed; leave the transition pending.'}
    $requirement=@{GateSha256=$Gate.Sha256;ScopeSha256=$Gate.Config.ScopeSha256}|ConvertTo-Json -Compress
    if(-not $alreadySwitched){
        $suffix=if($successor){'.successor-'}else{'.build-'}
        $nextPath=$Gate.RequiredPath+$suffix+$transition.Id.ToString('N')+'.tmp';Assert-NoLinks $nextPath
        $bytes=[Text.Encoding]::UTF8.GetBytes($requirement)
        if($successor -and (Test-Path -LiteralPath $nextPath)){
            if((Get-Item -LiteralPath $nextPath).Length -ne $bytes.Length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($nextPath))) -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))){throw 'Pending successor requirement bytes differ; preserve them for reconciliation.'}
        }else{
            $stream=[IO.FileStream]::new($nextPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        }
        [IO.File]::Move($nextPath,$Gate.RequiredPath,$true)
    }
    $installedGate=[pscustomobject]@{Path=(Join-Path $installRoot 'completion-workflow-gate.json');Sha256=$Gate.Sha256;Config=$Gate.Config;BuildTransition=$Gate.BuildTransition;SuccessorTransition=$Gate.SuccessorTransition}
    $command=if($successor){'complete-successor'}else{'complete-build'}
    $null=Invoke-CompletionGate $installedGate $command $transition.Id $transition.Sha256 $null $InstalledApp
}

function Get-RetainedCompletionAuthority($Gate){
    if(-not $Gate -or -not $reviewedResume -or -not $identity -or $identity.PSObject.Properties.Name -notcontains 'ChirpArticlePolicySha256'){return $null}
    $path=Join-Path $target 'chirp-article-admission-policy.json';Assert-NoLinks $path
    if((Get-Item -LiteralPath $path).Length -gt 8*1024*1024){throw 'Existing article scope exceeds its private bound.'}
    $bytes=[IO.File]::ReadAllBytes($path);$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if($sha -cne $identity.ChirpArticlePolicySha256.ToLowerInvariant()){throw 'The installed article scope changed; no implicit replacement allowed.'}
    $document=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($bytes))
    try{Assert-UniqueRecoveryJson $document.RootElement;Assert-ChirpPolicy $document.RootElement}finally{$document.Dispose()}
    # This is retention, not a fresh admission: require the SAME policy hash to
    # have a successful prior completion-admission event under this exact run.
    $null=Invoke-CompletionGate $Gate 'verify-completion' ([Guid]::Empty) $sha $null (Join-Path $package 'app') $path
    return [pscustomobject]@{Bytes=$bytes;Sha256=$sha}
}
function Assert-UniqueRecoveryJson($Element){
    if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object){
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $Element.EnumerateObject()){
            if(-not $seen.Add($property.Name)){throw 'Recovery policy has ambiguous properties.'}
            Assert-UniqueRecoveryJson $property.Value
        }
    }elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array){
        foreach($item in $Element.EnumerateArray()){Assert-UniqueRecoveryJson $item}
    }
}
function Assert-RecoveryRecord($Element,[hashtable]$Fields){
    if($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object -or @($Element.EnumerateObject()).Count -ne $Fields.Count){throw 'Recovery policy record shape is invalid.'}
    foreach($property in $Element.EnumerateObject()){
        if($property.Name -cnotin @($Fields.Keys)){throw 'Recovery policy contains an unknown record field.'}
        $value=$property.Value
        switch($Fields[$property.Name]){
            'String' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::String){throw 'Recovery policy string is invalid.'}}
            'Int32' {$null=$value.GetInt32()}
            'Int64' {$null=$value.GetInt64()}
            'Guid' {$null=$value.GetGuid()}
            'NonemptyGuid' {if($value.GetGuid() -eq [Guid]::Empty){throw 'Native identity is empty.'}}
            'NullableString' {if($value.ValueKind -notin @([Text.Json.JsonValueKind]::String,[Text.Json.JsonValueKind]::Null)){throw 'Nullable evidence string is invalid.'}}
            'Date' {$null=$value.GetDateTimeOffset()}
            'Array' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::Array){throw 'Recovery policy array is invalid.'}}
            'Any' {if($value.ValueKind -notin @([Text.Json.JsonValueKind]::Object,[Text.Json.JsonValueKind]::Null)){throw 'Nullable recovery record is invalid.'}}
            'Object' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'Recovery policy object is invalid.'}}
            default {throw 'Unknown recovery policy validation type.'}
        }
    }
}
function Write-RecoveryCanonicalRecord($Writer,$Element,[string]$Kind){
    $fields=switch($Kind){
        'Predecessor' {@('LogicalNarrationId:String','AccountId:String','OperationId:String','HistoricalKey:String','HistoricalStateSha256:String','ProjectId:String','DocumentId:Guid','RevisionId:Guid','TextSha256:String','OriginalPayloadSha256:String','AdmissionSha256:Empty','ExpiresAtUtc:Date','MaximumNewAttempts:Number','MaximumAdditionalReservationMinorUnits:Number','Successor:Successor')}
        'Successor' {@('PriorAuthorizationId:Guid','PriorChildKey:String','PriorStateSha256:String','PriorAdmissionSha256:String','FinancialStateSha256:String','SourceApprovalSha256:String','AllocationTransition:Allocation')}
        'Allocation' {@('PriorScopeSha256:String','PriorAllocationCeilingMinorUnits:Number','NewAllocationCeilingMinorUnits:Number','NewRemainingMinorUnits:Number','SourceApprovalSha256:String')}
        default {throw 'Unknown canonical recovery record.'}
    }
    $Writer.WriteStartObject()
    foreach($field in $fields){
        $name,$type=$field.Split(':',2);$value=$Element.GetProperty($name)
        switch($type){
            'String' {$Writer.WriteString([string]$name,[string]$value.GetString())}
            'Empty' {$Writer.WriteString([string]$name,[string]'')}
            'Guid' {$Writer.WriteString([string]$name,[guid]$value.GetGuid())}
            'Date' {$Writer.WriteString([string]$name,[DateTimeOffset]$value.GetDateTimeOffset())}
            'Number' {$Writer.WriteNumber([string]$name,[long]$value.GetInt64())}
            default {
                $Writer.WritePropertyName([string]$name)
                if($value.ValueKind -eq [Text.Json.JsonValueKind]::Null){$Writer.WriteNullValue()}
                else{Write-RecoveryCanonicalRecord $Writer $value $type}
            }
        }
    }
    $Writer.WriteEndObject()
}
function Get-RecoveryCanonicalGrantSha256($Element){
    $stream=[IO.MemoryStream]::new();$writer=[Text.Json.Utf8JsonWriter]::new($stream)
    try{
        Write-RecoveryCanonicalRecord $writer $Element 'Predecessor';$writer.Flush()
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray())).ToLowerInvariant()
    }finally{$writer.Dispose();$stream.Dispose()}
}
function Assert-RecoveryNestedPolicy($Root){
    Assert-RecoveryRecord $Root @{SchemaVersion='Int32';AppSha256='String';InfrastructureSha256='String';ProfileRootSha256='String';AccountId='String';ProjectId='String';Currency='String';Scale='Int32';ExpiresAtUtc='Date';Budget='Object';HistoricalAudio='Array';Predecessors='Array'}
    if($Root.GetProperty('SchemaVersion').GetInt32() -notin @(2,3)){throw 'Recovery policy requires bounded-grant schema2 or explicit-successor schema3.'}
    if([string]::IsNullOrWhiteSpace($Root.GetProperty('AccountId').GetString()) -or [string]::IsNullOrWhiteSpace($Root.GetProperty('ProjectId').GetString()) -or $Root.GetProperty('Currency').GetString() -cnotmatch '^[A-Z]{3}$' -or $Root.GetProperty('Scale').GetInt32() -notin 0..9){throw 'Recovery policy scope is invalid.'}
    foreach($name in @('AppSha256','InfrastructureSha256','ProfileRootSha256')){if($Root.GetProperty($name).GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy hash is invalid.'}}
    $budget=$Root.GetProperty('Budget')
    $budgetFields=@{ProjectCommittedMinorUnits='Int64';ProjectCeilingMinorUnits='Int64';AllocationCommittedMinorUnits='Int64';AllocationCeilingMinorUnits='Int64';ReviewedEvidenceSha256='String';QueueRows='Array';SpendRows='Array';RecoveryRows='Array'}
    if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$budgetFields['TransitionRows']='Array'}
    Assert-RecoveryRecord $budget $budgetFields
    if($budget.GetProperty('ProjectCommittedMinorUnits').GetInt64() -lt 0 -or $budget.GetProperty('AllocationCommittedMinorUnits').GetInt64() -lt 0 -or $budget.GetProperty('ProjectCeilingMinorUnits').GetInt64() -lt $budget.GetProperty('ProjectCommittedMinorUnits').GetInt64() -or $budget.GetProperty('AllocationCeilingMinorUnits').GetInt64() -lt $budget.GetProperty('AllocationCommittedMinorUnits').GetInt64() -or $budget.GetProperty('ReviewedEvidenceSha256').GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy budget bounds are invalid.'}
    $pinNames=@('QueueRows','SpendRows','RecoveryRows')
    if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$pinNames+='TransitionRows'}
    foreach($name in $pinNames){
        $pins=$budget.GetProperty($name)
        if($pins.GetArrayLength() -gt 10000){throw 'Recovery policy row bound exceeded.'}
        $identities=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($pin in $pins.EnumerateArray()){
            Assert-RecoveryRecord $pin @{Identity='String';Sha256='String'}
            $identity=$pin.GetProperty('Identity').GetString()
            if([string]::IsNullOrWhiteSpace($identity) -or -not $identities.Add($identity) -or $pin.GetProperty('Sha256').GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy financial pins are invalid.'}
        }
    }
    $historical=$Root.GetProperty('HistoricalAudio');$predecessors=$Root.GetProperty('Predecessors')
    if($historical.GetArrayLength() -gt 200 -or $predecessors.GetArrayLength() -gt 200){throw 'Recovery policy admission count exceeded.'}
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($entry in $historical.EnumerateArray()){
        Assert-RecoveryRecord $entry @{EvidenceId='String';Anchor='Object'}
        $id=$entry.GetProperty('EvidenceId').GetString()
        if([string]::IsNullOrWhiteSpace($id) -or -not $ids.Add($id)){throw 'Recovery policy evidence identity is invalid.'}
        $anchor=$entry.GetProperty('Anchor')
        Assert-RecoveryRecord $anchor @{ParentId='Guid';ParentRevisionId='Guid';ParentSha256='String';Ordinal='Int32';DocumentId='Guid';RevisionId='Guid';TextSha256='String';AccountId='String';OperationId='String';IdempotencyKey='String';LocalAttemptId='String';PayloadSha256='String';PayloadBytes='Int32';AudioSha256='String';AudioBytes='Int64';VoiceName='String';LanguageCode='String';EvidenceSha256='String'}
        if($anchor.GetProperty('AccountId').GetString() -cne $Root.GetProperty('AccountId').GetString() -or $anchor.GetProperty('OperationId').GetString() -cne 'synthesize-speech'){throw 'Recovery policy historical scope is invalid.'}
    }
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $grants=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($entry in $predecessors.EnumerateArray()){
        $fields=@{LogicalNarrationId='String';AccountId='String';OperationId='String';HistoricalKey='String';HistoricalStateSha256='String';ProjectId='String';DocumentId='Guid';RevisionId='Guid';TextSha256='String';OriginalPayloadSha256='String';AdmissionSha256='String';ExpiresAtUtc='Date';MaximumNewAttempts='Int32';MaximumAdditionalReservationMinorUnits='Int64'}
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$fields['Successor']='Object'}
        Assert-RecoveryRecord $entry $fields
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){
            if($Root.GetProperty('Currency').GetString() -cne 'USD' -or $Root.GetProperty('Scale').GetInt32() -ne 2){throw 'Successor admission requires USD cents.'}
            if($entry.GetProperty('ExpiresAtUtc').GetDateTimeOffset() -gt $Root.GetProperty('ExpiresAtUtc').GetDateTimeOffset()){throw 'Successor expiry exceeds policy expiry.'}
            if($historical.GetArrayLength() -ne 0 -or $predecessors.GetArrayLength() -ne 1 -or $entry.GetProperty('MaximumNewAttempts').GetInt32() -ne 1 -or $entry.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -ne 3){throw 'Successor admission requires one distinct one-attempt/three-cent grant and no import authority.'}
            $successor=$entry.GetProperty('Successor')
            Assert-RecoveryRecord $successor @{PriorAuthorizationId='Guid';PriorChildKey='String';PriorStateSha256='String';PriorAdmissionSha256='String';FinancialStateSha256='String';SourceApprovalSha256='String';AllocationTransition='Any'}
            if($successor.GetProperty('PriorAuthorizationId').GetGuid() -eq [guid]::Empty -or $successor.GetProperty('PriorChildKey').GetString() -cne ('recovery-google-tts:'+$successor.GetProperty('PriorAuthorizationId').GetGuid().ToString('N')) -or $successor.GetProperty('PriorAdmissionSha256').GetString() -ceq $entry.GetProperty('AdmissionSha256').GetString()){throw 'Successor lineage identity is invalid.'}
            foreach($name in @('PriorStateSha256','PriorAdmissionSha256','FinancialStateSha256','SourceApprovalSha256')){if($successor.GetProperty($name).GetString() -cnotmatch '^[a-f0-9]{64}$'){throw 'Successor digest is invalid.'}}
            $transition=$successor.GetProperty('AllocationTransition')
            if($transition.ValueKind -ne [Text.Json.JsonValueKind]::Null){
                Assert-RecoveryRecord $transition @{PriorScopeSha256='String';PriorAllocationCeilingMinorUnits='Int64';NewAllocationCeilingMinorUnits='Int64';NewRemainingMinorUnits='Int64';SourceApprovalSha256='String'}
                if($transition.GetProperty('PriorScopeSha256').GetString() -cnotmatch '^[a-f0-9]{64}$' -or $transition.GetProperty('SourceApprovalSha256').GetString() -cne $successor.GetProperty('SourceApprovalSha256').GetString() -or $transition.GetProperty('PriorAllocationCeilingMinorUnits').GetInt64() -lt 0 -or $transition.GetProperty('NewAllocationCeilingMinorUnits').GetInt64() -le $transition.GetProperty('PriorAllocationCeilingMinorUnits').GetInt64() -or $transition.GetProperty('NewAllocationCeilingMinorUnits').GetInt64() -ne $budget.GetProperty('AllocationCeilingMinorUnits').GetInt64() -or $transition.GetProperty('NewRemainingMinorUnits').GetInt64() -ne ($budget.GetProperty('AllocationCeilingMinorUnits').GetInt64()-$budget.GetProperty('AllocationCommittedMinorUnits').GetInt64())){throw 'Allocation transition is invalid.'}
            }
        }
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3 -and $entry.GetProperty('AdmissionSha256').GetString() -cne (Get-RecoveryCanonicalGrantSha256 $entry)){throw 'Successor grant digest does not bind its full canonical authority.'}
        if($entry.GetProperty('MaximumNewAttempts').GetInt32() -notin 1..200 -or $entry.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -lt 0 -or $entry.GetProperty('AdmissionSha256').GetString() -notmatch '^[a-fA-F0-9]{64}$' -or -not $grants.Add($entry.GetProperty('AdmissionSha256').GetString())){throw 'Recovery policy grant bounds or identity are invalid.'}
        if(-not $keys.Add($entry.GetProperty('HistoricalKey').GetString()) -or $entry.GetProperty('AccountId').GetString() -cne $Root.GetProperty('AccountId').GetString() -or $entry.GetProperty('ProjectId').GetString() -cne $Root.GetProperty('ProjectId').GetString() -or $entry.GetProperty('OperationId').GetString() -cne 'synthesize-speech'){throw 'Recovery policy predecessor scope is invalid.'}
    }
}
function Get-ReviewedRecoveryPolicy([string]$Profile){
    if(-not $ReviewedRecoveryPolicyPath){return $null}
    if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'Optional recovery policy admission requires PowerShell 7 on .NET 8 or later. Ordinary updates without a recovery policy remain supported in Windows PowerShell.'}
    if(-not $Profile -or $ReviewedRecoveryPolicySha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy requires an inspected existing profile and independent hash.'}
    $path=(Resolve-Path -LiteralPath $ReviewedRecoveryPolicyPath).Path
    Assert-NoLinks $path
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -lt 1 -or $stream.Length -gt 1048576){throw 'Recovery policy exceeds its reviewed size bound.'}
        $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)
    }finally{$stream.Dispose()}
    $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if($sha -ne $ReviewedRecoveryPolicySha256){throw 'Recovery policy does not match the independently approved SHA256.'}
    $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=16
    $json=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes),$options)
    try{
        Assert-UniqueRecoveryJson $json.RootElement
        $root=$json.RootElement
        Assert-RecoveryNestedPolicy $root
        $allowed=@('SchemaVersion','AppSha256','InfrastructureSha256','ProfileRootSha256','AccountId','ProjectId','Currency','Scale','ExpiresAtUtc','Budget','HistoricalAudio','Predecessors')
        if(@($root.EnumerateObject()).Count -ne $allowed.Count -or @($root.EnumerateObject() | Where-Object {$_.Name -cnotin $allowed}).Count){throw 'Recovery policy schema is incomplete or unknown.'}
        if($root.GetProperty('SchemaVersion').GetInt32() -notin @(2,3) -or $root.GetProperty('ExpiresAtUtc').GetDateTimeOffset() -le [DateTimeOffset]::UtcNow){throw 'Recovery policy is incompatible or expired.'}
        if($root.GetProperty('InfrastructureSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash){throw 'Recovery policy belongs to another exact build.'}
        if($root.GetProperty('AppSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash){throw 'Recovery policy belongs to another exact app build.'}
        $canonical=[IO.Path]::GetFullPath($Profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()
        $profileSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
        if($root.GetProperty('ProfileRootSha256').GetString() -ne $profileSha){throw 'Recovery policy belongs to another profile.'}
    }finally{$json.Dispose()}
    return [pscustomobject]@{Bytes=$bytes;Sha256=$sha}
}
function Assert-ChirpPolicyHashes($Element){
    if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object){
        foreach($property in $Element.EnumerateObject()){
            if($property.Name -cmatch 'Sha256$' -and $property.Value.ValueKind -ne [Text.Json.JsonValueKind]::Null -and $property.Value.GetString() -cnotmatch '^[a-f0-9]{64}$'){throw 'Article evidence hash is invalid.'}
            Assert-ChirpPolicyHashes $property.Value
        }
    }elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($item in $Element.EnumerateArray()){Assert-ChirpPolicyHashes $item}}
}
function Assert-ChirpRange($Range){
    Assert-RecoveryRecord $Range @{StartScalar='Int32';EndScalarExclusive='Int32';TextSha256='String';ReusedReceiptSha256='NullableString'}
    if($Range.GetProperty('StartScalar').GetInt32() -lt 0 -or $Range.GetProperty('EndScalarExclusive').GetInt32() -le $Range.GetProperty('StartScalar').GetInt32()){throw 'Article coverage range is invalid.'}
}
function Assert-ChirpSuccessorGrant($Root){
    if(@($Root.EnumerateObject().Name) -cnotcontains 'SuccessorGrant'){return}
    $grant=$Root.GetProperty('SuccessorGrant')
    Assert-RecoveryRecord $grant @{GrantId='NonemptyGuid';RootAuthorizationId='NonemptyGuid';ParentAuthorizationId='NonemptyGuid';PlanSha256='String';CoverageOrdinal='Int32';ParentKey='String';ParentAttemptRowSha256='String';ParentQueueRowSha256='String';ParentLocalAttemptId='String';CredentialReferenceId='String';ReviewedFailureEvidenceSha256='String';MaximumNewAttempts='Int32';MaximumAdditionalReservationMinorUnits='Int64'}
    if($grant.GetProperty('MaximumNewAttempts').GetInt32() -ne 1 -or $grant.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -ne 1 -or
        $Root.GetProperty('MaximumNewAttempts').GetInt32() -ne 1 -or $Root.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -ne 1 -or
        $Root.GetProperty('Articles').GetArrayLength() -ne 1 -or $grant.GetProperty('ParentLocalAttemptId').GetString() -cnotmatch '^[a-f0-9]{32}$'){
        throw 'Diagnostic successor requires one exact consumed parent, one attempt and one new cent.'
    }
    $key=$grant.GetProperty('ParentKey').GetString();$credential=$grant.GetProperty('CredentialReferenceId').GetString()
    if(-not $key.StartsWith('chirp-article:',[StringComparison]::Ordinal) -or $key.Length -gt 128 -or [string]::IsNullOrWhiteSpace($credential) -or $credential.Length -gt 128){throw 'Successor parent or credential binding is invalid.'}
    $article=$Root.GetProperty('Articles')[0];$segments=$article.GetProperty('AllowedSegments')
    if($segments.GetArrayLength() -ne 1 -or $segments[0].GetProperty('CoverageOrdinal').GetInt32() -ne $grant.GetProperty('CoverageOrdinal').GetInt32() -or
        $segments[0].GetProperty('MaximumMinorUnits').GetInt64() -ne 1 -or $article.GetProperty('Proposal').GetProperty('PlanSha256').GetString() -cne $grant.GetProperty('PlanSha256').GetString()){
        throw 'Diagnostic successor cannot widen or change the selected article coverage.'
    }
}
function Assert-ChirpPolicy($Root,[DateTimeOffset]$ValidationTime=[DateTimeOffset]::UtcNow){
    $fields=@{SchemaVersion='Int32';AppSha256='String';InfrastructureSha256='String';ProfileRootSha256='String';AccountId='String';ProjectId='String';Currency='String';Scale='Int32';ExpiresAtUtc='Date';SourceApprovalSha256='String';Budget='Object';ScopeRows='Array';PriorArticleAttemptRows='Array';Articles='Array';MaximumNewAttempts='Int32';MaximumAdditionalReservationMinorUnits='Int64'}
    $legacy=@($Root.EnumerateObject().Name) -ccontains 'LegacyAssociations'
    if($legacy){$fields.LegacyAssociations='Array';$fields.LegacyAssociationRows='Array'}
    $schema=$Root.GetProperty('SchemaVersion').GetInt32()
    if($schema -eq 2){
        $fields.PriorSuccessorAttemptRows='Array'
        if(@($Root.EnumerateObject().Name) -ccontains 'SuccessorGrant'){$fields.SuccessorGrant='Object'}
    }
    Assert-RecoveryRecord $Root $fields
    if($legacy){Assert-ChirpLegacyAssociations $Root}
    if($schema -notin @(1,2) -or $Root.GetProperty('Currency').GetString() -cne 'USD' -or $Root.GetProperty('Scale').GetInt32() -ne 2 -or
        $Root.GetProperty('ExpiresAtUtc').GetDateTimeOffset() -le $ValidationTime -or ($Root.GetProperty('MaximumNewAttempts').GetInt32() -lt 1 -or $Root.GetProperty('MaximumNewAttempts').GetInt32() -gt 10000) -or
        $Root.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -lt 1){throw 'Article grant identity, expiry or limits are invalid.'}
    if([string]::IsNullOrWhiteSpace($Root.GetProperty('AccountId').GetString()) -or [string]::IsNullOrWhiteSpace($Root.GetProperty('ProjectId').GetString())){throw 'Article account/project is missing.'}
    Assert-ChirpPolicyHashes $Root
    $budget=$Root.GetProperty('Budget')
    Assert-RecoveryRecord $budget @{ProjectCommittedMinorUnits='Int64';ProjectCeilingMinorUnits='Int64';AllocationCommittedMinorUnits='Int64';AllocationCeilingMinorUnits='Int64';ReviewedEvidenceSha256='String';QueueRows='Array';SpendRows='Array';RecoveryRows='Array';TransitionRows='Array'}
    $maximum=$Root.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64()
    foreach($kind in @('Project','Allocation')){
        $committed=$budget.GetProperty($kind+'CommittedMinorUnits').GetInt64();$ceiling=$budget.GetProperty($kind+'CeilingMinorUnits').GetInt64()
        if($committed -lt 0 -or $ceiling -lt $committed -or $maximum -gt $ceiling-$committed){throw 'Article allocation omits retained commitments or exceeds its ceiling.'}
    }
    $pinSets=@($Root.GetProperty('ScopeRows'),$Root.GetProperty('PriorArticleAttemptRows'),$budget.GetProperty('QueueRows'),$budget.GetProperty('SpendRows'),$budget.GetProperty('RecoveryRows'),$budget.GetProperty('TransitionRows'))
    if($schema -eq 2){$pinSets+=@($Root.GetProperty('PriorSuccessorAttemptRows'));Assert-ChirpSuccessorGrant $Root}
    foreach($pins in $pinSets){
        if($pins.GetArrayLength() -gt 10000){throw 'Article financial baseline exceeds its bound.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($pin in $pins.EnumerateArray()){
            Assert-RecoveryRecord $pin @{Identity='String';Sha256='String'}
            if($pin.GetProperty('Identity').GetString() -cnotmatch '^[a-f0-9]{64}$' -or -not $seen.Add($pin.GetProperty('Identity').GetString())){throw 'Article financial pins are ambiguous.'}
        }
    }
    $articles=$Root.GetProperty('Articles');if($articles.GetArrayLength() -lt 1 -or $articles.GetArrayLength() -gt 200){throw 'Article admission count is invalid.'}
    $documents=[Collections.Generic.HashSet[Guid]]::new()
    foreach($article in $articles.EnumerateArray()){
        Assert-RecoveryRecord $article @{Proposal='Object';Lineage='Object';ReusedAudio='Array';AllowedSegments='Array'}
        $plan=$article.GetProperty('Proposal')
        $planFields=@{DocumentId='NonemptyGuid';RevisionId='NonemptyGuid';SourceSha256='String';SourceScalars='Int32';LanguageCode='String';VoiceName='String';PlannerVersion='String';PlanSha256='String';Parts='Array'}
        $refined=@($plan.EnumerateObject().Name) -ccontains 'Refinement'
        if($refined){$planFields.Refinement='Object'}
        Assert-RecoveryRecord $plan $planFields
        if($refined){
            if($schema -ne 2 -or $plan.GetProperty('PlannerVersion').GetString() -cne 'chirp3-one-failed-range-existing-clauses-v1'){throw 'Refined article requires explicit schema and compiler version.'}
            $refinement=$plan.GetProperty('Refinement')
            Assert-RecoveryRecord $refinement @{Parent='Object';FailedCoverageOrdinal='Int32';FailedAuthorizationId='NonemptyGuid';FailedAttemptRowSha256='String';FailedQueueRowSha256='String';FailedLocalAttemptId='String'}
            if($refinement.GetProperty('FailedLocalAttemptId').GetString() -cnotmatch '^[a-f0-9]{32}$' -or
                -not @($Root.GetProperty('PriorArticleAttemptRows').EnumerateArray()|Where-Object {$_.GetProperty('Sha256').GetString() -ceq $refinement.GetProperty('FailedAttemptRowSha256').GetString()}).Count -or
                -not @($budget.GetProperty('QueueRows').EnumerateArray()|Where-Object {$_.GetProperty('Sha256').GetString() -ceq $refinement.GetProperty('FailedQueueRowSha256').GetString()}).Count){throw 'Refinement failed-attempt evidence is absent from retained baseline.'}
            $parentPlan=$refinement.GetProperty('Parent')
            $parentFields=$planFields.Clone();$parentFields.Remove('Refinement')
            Assert-RecoveryRecord $parentPlan $parentFields
            if($parentPlan.GetProperty('PlannerVersion').GetString() -cne 'chirp3-existing-boundaries-v1' -or $parentPlan.GetProperty('Parts').GetArrayLength() -lt 1 -or $parentPlan.GetProperty('Parts').GetArrayLength() -gt 10000 -or
                $refinement.GetProperty('FailedCoverageOrdinal').GetInt32() -lt 1 -or $refinement.GetProperty('FailedCoverageOrdinal').GetInt32() -gt $parentPlan.GetProperty('Parts').GetArrayLength()){throw 'Refinement predecessor is absent, nested or unbounded.'}
            foreach($field in @('DocumentId','RevisionId','SourceSha256','SourceScalars','LanguageCode','VoiceName')){if($plan.GetProperty($field).GetRawText() -cne $parentPlan.GetProperty($field).GetRawText()){throw 'Refinement changes original source or voice.'}}
            foreach($oldPart in $parentPlan.GetProperty('Parts').EnumerateArray()){
                Assert-RecoveryRecord $oldPart @{CoverageOrdinal='Int32';Range='Object';PayloadSha256='NullableString';PayloadBytes='Int32'}
                Assert-ChirpRange $oldPart.GetProperty('Range')
            }
        }
        if(-not $documents.Add($plan.GetProperty('DocumentId').GetGuid()) -or ($plan.GetProperty('SourceScalars').GetInt32() -lt 1 -or $plan.GetProperty('SourceScalars').GetInt32() -gt 1000000) -or (-not $refined -and $plan.GetProperty('PlannerVersion').GetString() -cne 'chirp3-existing-boundaries-v1') -or
            -not $plan.GetProperty('VoiceName').GetString().Contains('-Chirp3-HD-')){throw 'Native article plan identity is incompatible.'}
        $parts=$plan.GetProperty('Parts');if($parts.GetArrayLength() -lt 1 -or $parts.GetArrayLength() -gt 10000){throw 'Internal coverage count is invalid.'}
        $ordinal=0;$cursor=0;$reusable=[Collections.Generic.HashSet[int]]::new()
        foreach($part in $parts.EnumerateArray()){
            Assert-RecoveryRecord $part @{CoverageOrdinal='Int32';Range='Object';PayloadSha256='NullableString';PayloadBytes='Int32'}
            $ordinal++;if($part.GetProperty('CoverageOrdinal').GetInt32() -ne $ordinal){throw 'Internal coverage order is invalid.'}
            $range=$part.GetProperty('Range');Assert-ChirpRange $range
            if($range.GetProperty('StartScalar').GetInt32() -ne $cursor){throw 'Internal coverage has a gap or overlap.'};$cursor=$range.GetProperty('EndScalarExclusive').GetInt32()
            if($range.GetProperty('ReusedReceiptSha256').ValueKind -eq [Text.Json.JsonValueKind]::Null){
                if($part.GetProperty('PayloadSha256').ValueKind -ne [Text.Json.JsonValueKind]::String -or ($part.GetProperty('PayloadBytes').GetInt32() -lt 1 -or $part.GetProperty('PayloadBytes').GetInt32() -gt 5000) -or $cursor-$range.GetProperty('StartScalar').GetInt32() -gt 333){throw 'Internal compiled request is invalid.'}
            }else{
                if($part.GetProperty('PayloadSha256').ValueKind -ne [Text.Json.JsonValueKind]::Null -or $part.GetProperty('PayloadBytes').GetInt32() -ne 0){throw 'Accepted coverage cannot be regenerated.'};$null=$reusable.Add($ordinal)
            }
        }
        if($cursor -ne $plan.GetProperty('SourceScalars').GetInt32()){throw 'Internal coverage changes the article cutoff.'}
        $allowed=$article.GetProperty('AllowedSegments');if($allowed.GetArrayLength() -lt 1 -or $allowed.GetArrayLength() -gt 10000){throw 'No exact internal request scope is admitted.'}
        $seen=[Collections.Generic.HashSet[int]]::new()
        foreach($item in $allowed.EnumerateArray()){
            Assert-RecoveryRecord $item @{CoverageOrdinal='Int32';MaximumMinorUnits='Int64'}
            $n=$item.GetProperty('CoverageOrdinal').GetInt32();$cost=$item.GetProperty('MaximumMinorUnits').GetInt64()
            if($n -lt 1 -or $n -gt $ordinal -or -not $seen.Add($n) -or $reusable.Contains($n) -or $cost -lt 1 -or $cost -gt $maximum){throw 'Internal request allowance is invalid or duplicates accepted coverage.'}
        }
        Assert-ChirpLineage $article.GetProperty('Lineage')
        foreach($audio in $article.GetProperty('ReusedAudio').EnumerateArray()){
            $audioFields=@{ParentDocumentId='NonemptyGuid';ParentRevisionId='NonemptyGuid';ReceiptSha256='String';Range='Object';Audio='Object'}
            if(@($audio.EnumerateObject().Name) -ccontains 'SourcePlanSha256' -or @($audio.EnumerateObject().Name) -ccontains 'SourceAuthorizationId'){
                if(-not $refined){throw 'Internal accepted audio reuse requires the explicit refined plan.'}
                $audioFields.SourcePlanSha256='String';$audioFields.SourceAuthorizationId='NonemptyGuid'
            }
            Assert-RecoveryRecord $audio $audioFields
            Assert-ChirpRange $audio.GetProperty('Range')
            Assert-RecoveryRecord $audio.GetProperty('Audio') @{DocumentId='NonemptyGuid';RevisionId='NonemptyGuid';TextSha256='String';AccountId='String';OperationId='String';IdempotencyKey='String';LocalAttemptId='String';PayloadSha256='String';AudioPath='String';AudioBytes='Int64';AudioSha256='String';VoiceName='String';LanguageCode='String';HistoricalEvidenceSha256='NullableString';HistoricalAnchorSha256='NullableString'}
        }
    }
}
function Assert-ChirpLegacyAssociations($Root){
    $items=$Root.GetProperty('LegacyAssociations');$pins=$Root.GetProperty('LegacyAssociationRows')
    if($items.GetArrayLength() -lt 1 -or $items.GetArrayLength() -gt 200 -or $pins.GetArrayLength() -gt 10000){throw 'Historical association count is invalid.'}
    $seenPins=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($pin in $pins.EnumerateArray()){
        Assert-RecoveryRecord $pin @{Identity='String';Sha256='String'}
        if($pin.GetProperty('Identity').GetString() -cnotmatch '^[a-f0-9]{64}$' -or -not $seenPins.Add($pin.GetProperty('Identity').GetString())){throw 'Historical audit pins are ambiguous.'}
    }
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$spends=[Collections.Generic.HashSet[Guid]]::new();$retained=0L
    foreach($a in $items.EnumerateArray()){
        Assert-RecoveryRecord $a @{PlanSha256='String';DocumentId='NonemptyGuid';RevisionId='NonemptyGuid';StartScalar='Int32';EndScalarExclusive='Int32';TextSha256='String';AccountId='String';ProjectId='String';OperationId='String';IdempotencyKey='String';QueueRowSha256='String';SpendAuthorizationId='NonemptyGuid';SpendRowSha256='String';OriginalPayloadSha256='String';OriginalPayloadBytes='Int32';RetainedReservationMinorUnits='Int64';ReviewedEvidenceSha256='String'}
        $key=$a.GetProperty('IdempotencyKey').GetString();$cost=$a.GetProperty('RetainedReservationMinorUnits').GetInt64()
        if([string]::IsNullOrWhiteSpace($key) -or $key.Length -gt 128 -or $key.StartsWith('chirp-article:',[StringComparison]::Ordinal) -or -not $keys.Add($key) -or -not $spends.Add($a.GetProperty('SpendAuthorizationId').GetGuid()) -or
            $a.GetProperty('AccountId').GetString() -cne $Root.GetProperty('AccountId').GetString() -or $a.GetProperty('ProjectId').GetString() -cne $Root.GetProperty('ProjectId').GetString() -or $a.GetProperty('OperationId').GetString() -cne 'synthesize-speech' -or
            $cost -lt 1 -or $a.GetProperty('OriginalPayloadBytes').GetInt32() -lt 1 -or $a.GetProperty('OriginalPayloadBytes').GetInt32() -gt 5000 -or $a.GetProperty('StartScalar').GetInt32() -lt 0 -or $a.GetProperty('EndScalarExclusive').GetInt32() -le $a.GetProperty('StartScalar').GetInt32()){throw 'Historical association identity or reservation is invalid.'}
        $retained=[long]($retained+$cost)
        $article=@($Root.GetProperty('Articles').EnumerateArray() | Where-Object {$_.GetProperty('Proposal').GetProperty('PlanSha256').GetString() -ceq $a.GetProperty('PlanSha256').GetString()})
        if($article.Count -ne 1){throw 'Historical association article is missing or ambiguous.'}
        $native=@($article[0].GetProperty('Lineage').GetProperty('History').EnumerateArray() | Where-Object {$_.GetProperty('DocumentId').GetGuid() -eq $a.GetProperty('DocumentId').GetGuid() -and $_.GetProperty('RevisionId').GetGuid() -eq $a.GetProperty('RevisionId').GetGuid() -and $_.GetProperty('StartScalar').GetInt32() -eq $a.GetProperty('StartScalar').GetInt32() -and $_.GetProperty('EndScalarExclusive').GetInt32() -eq $a.GetProperty('EndScalarExclusive').GetInt32() -and $_.GetProperty('TextSha256').GetString() -ceq $a.GetProperty('TextSha256').GetString()})
        if($native.Count -ne 1 -or @($article[0].GetProperty('Lineage').GetProperty('History').EnumerateArray() | ForEach-Object {$_.GetProperty('Attempts').EnumerateArray()} | Where-Object {$_.GetProperty('IdempotencyKey').GetString() -ceq $key}).Count){throw 'Historical association duplicates or changes native coverage.'}
        foreach($pair in @(@('QueueRows','QueueRowSha256'),@('SpendRows','SpendRowSha256'))){
            if(-not @($Root.GetProperty('Budget').GetProperty($pair[0]).EnumerateArray() | Where-Object {$_.GetProperty('Sha256').GetString() -ceq $a.GetProperty($pair[1]).GetString()}).Count){throw 'Historical association is absent from independent financial pins.'}
        }
    }
    if($retained -gt $Root.GetProperty('Budget').GetProperty('ProjectCommittedMinorUnits').GetInt64() -or $retained -gt $Root.GetProperty('Budget').GetProperty('AllocationCommittedMinorUnits').GetInt64()){throw 'Historical holds are missing from the retained baseline.'}
}
function Assert-ChirpLineage($Lineage){
    Assert-RecoveryRecord $Lineage @{History='Array';Ranges='Array'}
    $history=$Lineage.GetProperty('History');if($history.GetArrayLength() -lt 1 -or $history.GetArrayLength() -gt 10000){throw 'Historical article lineage is absent or unbounded.'}
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($old in $history.EnumerateArray()){
        Assert-RecoveryRecord $old @{DocumentId='NonemptyGuid';RevisionId='NonemptyGuid';StartScalar='Int32';EndScalarExclusive='Int32';TextSha256='String';Attempts='Array'}
        foreach($attempt in $old.GetProperty('Attempts').EnumerateArray()){
            Assert-RecoveryRecord $attempt @{IdempotencyKey='String';StateSha256='String';RetainedReservationMinorUnits='Int64'}
            if($keys.Count -ge 10000 -or -not $keys.Add($attempt.GetProperty('IdempotencyKey').GetString()) -or $attempt.GetProperty('RetainedReservationMinorUnits').GetInt64() -lt 0){throw 'Historical attempt lineage is invalid.'}
        }
    }
    if($Lineage.GetProperty('Ranges').GetArrayLength() -gt 10000){throw 'Historical indexed coverage exceeds its bound.'}
    foreach($range in $Lineage.GetProperty('Ranges').EnumerateArray()){
        Assert-RecoveryRecord $range @{CoverageOrdinal='Int32';HistoricalRangeIndexes='Array'}
        foreach($index in $range.GetProperty('HistoricalRangeIndexes').EnumerateArray()){if($index.GetInt32() -lt 0 -or $index.GetInt32() -ge $history.GetArrayLength()){throw 'Historical overlap index is invalid.'}}
    }
}
function Get-ReviewedChirpPolicy([string]$Profile){
    if(-not $ReviewedChirpArticlePolicyPath){return $null}
    if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'Article policy admission requires PowerShell 7 on .NET 8 or later.'}
    if(-not $Profile -or $ReviewedChirpArticlePolicySha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Article policy requires an inspected profile and independently approved hash.'}
    $path=(Resolve-Path -LiteralPath $ReviewedChirpArticlePolicyPath).Path;Assert-NoLinks $path
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($stream.Length -lt 1 -or $stream.Length -gt 8388608){throw 'Article policy exceeds its bound.'};$bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)}finally{$stream.Dispose()}
    $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if($sha -ne $ReviewedChirpArticlePolicySha256){throw 'Article policy differs from the independently approved hash.'}
    $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=24;$json=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes),$options)
    try{
        Assert-UniqueRecoveryJson $json.RootElement;Assert-ChirpPolicy $json.RootElement;$root=$json.RootElement
        if($root.GetProperty('AppSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash -or
            $root.GetProperty('InfrastructureSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash){throw 'Article policy belongs to another exact build.'}
        $canonical=[IO.Path]::GetFullPath($Profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()
        $profileSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
        if($root.GetProperty('ProfileRootSha256').GetString() -ne $profileSha){throw 'Article policy belongs to another profile.'}
    }finally{$json.Dispose()}
    return [pscustomobject]@{Bytes=$bytes;Sha256=$sha}
}
function Assert-ChirpResumePolicy($Root){
    $fields=@{SchemaVersion='Int32';MappingId='NonemptyGuid';AppSha256='String';InfrastructureSha256='String';ProfileRootSha256='String';SourceApprovalSha256='String';ExpiresAtUtc='Date';HistoricalPolicy='Object';AuthorizationId='NonemptyGuid';OriginalAttemptRowSha256='String';CleanQueueRowSha256='String';FinancialSha256='String';OriginalApprovedAtUtc='Date';OriginalEnvelope='Object';ExecutionEnvelope='Object';CatalogSnapshotId='NonemptyGuid';CatalogExpiresAtUtc='Date';ProviderEvidenceSha256='String';SelectionSha256='String';RuntimeEvidenceSha256='String';RequestIdentitySha256='String';MaximumExistingDispatches='Int32';MaximumNewReservations='Int32';MaximumAdditionalReservationMinorUnits='Int64'}
    if(@($Root.EnumerateObject().Name) -ccontains 'PriorMappingSha256'){$fields.PriorMappingSha256='String'}
    Assert-RecoveryRecord $Root $fields
    $now=[DateTimeOffset]::UtcNow;$expiry=$Root.GetProperty('ExpiresAtUtc').GetDateTimeOffset();$catalogExpiry=$Root.GetProperty('CatalogExpiresAtUtc').GetDateTimeOffset();$approved=$Root.GetProperty('OriginalApprovedAtUtc').GetDateTimeOffset()
    if($Root.GetProperty('SchemaVersion').GetInt32() -ne 1 -or $expiry -le $now -or $catalogExpiry -le $now -or $expiry -gt $catalogExpiry -or $approved -gt $now -or
        $expiry.Offset -ne [TimeSpan]::Zero -or $catalogExpiry.Offset -ne [TimeSpan]::Zero -or $approved.Offset -ne [TimeSpan]::Zero -or
        $Root.GetProperty('MaximumExistingDispatches').GetInt32() -ne 1 -or $Root.GetProperty('MaximumNewReservations').GetInt32() -ne 0 -or $Root.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -ne 0){throw 'Resume authority must allow one existing dispatch, zero new reservations, and a fresh bounded expiry.'}
    Assert-ChirpPolicyHashes $Root
    foreach($property in $Root.EnumerateObject()){
        if($property.Name.EndsWith('Sha256',[StringComparison]::Ordinal) -and $property.Value.GetString() -cnotmatch '^[a-f0-9]{64}$'){throw 'Resume hashes must use canonical lowercase SHA256.'}
    }
    $history=$Root.GetProperty('HistoricalPolicy')
    # Historical shape is checked at the explicitly pinned original reservation time, never admitted as current paid authority.
    Assert-ChirpPolicy $history $approved
    if($history.GetProperty('ProfileRootSha256').GetString() -cne $Root.GetProperty('ProfileRootSha256').GetString() -or $history.GetProperty('SourceApprovalSha256').GetString() -ceq $Root.GetProperty('SourceApprovalSha256').GetString()){throw 'Resume requires the same profile and distinct fresh source approval.'}
    $fields=@{AccountId='String';CredentialReferenceId='String';CapabilityProvenanceId='String';PricingProvenanceId='String';RequestRevision='Int32';VoiceName='String';AudioEncoding='String';CompiledPayloadSha256='String';CompiledPayloadBytes='Int32';RecoveryAuthorizationId='Any';ChirpAuthorizationId='NonemptyGuid'}
    $original=$Root.GetProperty('OriginalEnvelope');$execution=$Root.GetProperty('ExecutionEnvelope')
    foreach($envelope in @($original,$execution)){
        Assert-RecoveryRecord $envelope $fields
        if($envelope.GetProperty('RecoveryAuthorizationId').ValueKind -ne [Text.Json.JsonValueKind]::Null -or $envelope.GetProperty('ChirpAuthorizationId').GetGuid() -ne $Root.GetProperty('AuthorizationId').GetGuid() -or
            $envelope.GetProperty('RequestRevision').GetInt32() -lt 0 -or $envelope.GetProperty('AudioEncoding').GetString() -cne 'MP3' -or $envelope.GetProperty('CompiledPayloadSha256').GetString() -cnotmatch '^[a-f0-9]{64}$' -or
            $envelope.GetProperty('CompiledPayloadBytes').GetInt32() -lt 1 -or $envelope.GetProperty('CompiledPayloadBytes').GetInt32() -gt 5000){throw 'Resume envelope identity or payload is invalid.'}
    }
    foreach($name in $fields.Keys | Where-Object {$_ -ne 'RequestRevision'}){
        if($original.GetProperty($name).GetRawText() -cne $execution.GetProperty($name).GetRawText()){throw 'Resume may only change the explicitly reviewed catalog-derived request revision.'}
    }
    if($original.GetProperty('AccountId').GetString() -cne $history.GetProperty('AccountId').GetString()){throw 'Resume account differs from retained article history.'}
    $matches=@($history.GetProperty('Articles').EnumerateArray() | Where-Object {
        $article=$_;$plan=$article.GetProperty('Proposal')
        $plan.GetProperty('VoiceName').GetString() -ceq $original.GetProperty('VoiceName').GetString() -and @($plan.GetProperty('Parts').EnumerateArray() | Where-Object {
            $part=$_;$ordinal=$part.GetProperty('CoverageOrdinal').GetInt32()
            $part.GetProperty('PayloadSha256').GetString() -ceq $original.GetProperty('CompiledPayloadSha256').GetString() -and
                $part.GetProperty('PayloadBytes').GetInt32() -eq $original.GetProperty('CompiledPayloadBytes').GetInt32() -and
                @($article.GetProperty('AllowedSegments').EnumerateArray() | Where-Object {$_.GetProperty('CoverageOrdinal').GetInt32() -eq $ordinal}).Count -eq 1
        }).Count -gt 0
    })
    if($matches.Count -eq 0){throw 'Retained voice/payload is outside the original exact article allowance.'}
}
function Get-ReviewedChirpResumePolicy([string]$Profile){
    if(-not $ReviewedChirpResumePolicyPath){return $null}
    if(-not $Profile -or $ReviewedChirpResumePolicySha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Resume policy requires an inspected profile and independently approved hash.'}
    $path=(Resolve-Path -LiteralPath $ReviewedChirpResumePolicyPath).Path;Assert-NoLinks $path
    if((Get-Item -LiteralPath $path).Length -gt 8MB){throw 'Resume policy exceeds its review bound.'}
    $bytes=[IO.File]::ReadAllBytes($path);$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if($sha -ne $ReviewedChirpResumePolicySha256){throw 'Resume policy differs from the independently approved bytes.'}
    $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=24;$json=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes),$options)
    try{
        Assert-UniqueRecoveryJson $json.RootElement;Assert-ChirpResumePolicy $json.RootElement;$root=$json.RootElement
        if($root.GetProperty('AppSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash -or
            $root.GetProperty('InfrastructureSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash){throw 'Resume policy belongs to another exact build.'}
        $canonical=[IO.Path]::GetFullPath($Profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()
        $profileSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
        if($root.GetProperty('ProfileRootSha256').GetString() -ne $profileSha){throw 'Resume policy belongs to another profile.'}
    }finally{$json.Dispose()}
    return [pscustomobject]@{Bytes=$bytes;Sha256=$sha}
}
function Get-AllowedNoticeFiles {
    $inventoryPath=Join-Path $package 'licenses/third-party/components.json'
    # Refresh this pin only alongside the reviewed source inventory and its fixture tests.
    # The package itself cannot expand the installer's notice-file allowlist.
    $inventorySha256='d3f95b877d4ee8230a7121cd8126ebd1321340d57e00ae2aee058f1c0e2089d7'
    if((Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash -ne $inventorySha256){throw 'Unexpected third-party inventory identity.'}
    $inventory=Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if($inventory.schema_version -ne 1 -or @($inventory.components).Count -eq 0){throw 'Invalid third-party inventory.'}
    $allowed=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($component in $inventory.components){
        if(@($component.notice_files).Count -eq 0){throw 'Missing third-party notice records.'}
        foreach($notice in $component.notice_files){
            $relative=[string]$notice.path
            $segments=$relative.Split('/')
            if($relative -notmatch '^(texts|upstream)/' -or $relative -match '[\\:\x00-\x1f*?"<>|]' -or
                ($segments | Where-Object {$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ')})){
                throw 'Unsafe third-party notice path.'
            }
            if(([IO.Path]::GetExtension($relative) -notin @('','.txt','.md') -and $relative -cne 'upstream/tmds-Tmds.DBus-COPYING') -or $notice.sha256 -notmatch '^[a-fA-F0-9]{64}$'){
                throw 'Unexpected third-party notice type or hash.'
            }
            $entry='licenses/third-party/'+$relative
            $path=[IO.Path]::GetFullPath((Join-Path $package $entry))
            if(-not $path.StartsWith($packagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Third-party notice path escapes the package.'}
            if($allowed.ContainsKey($entry) -and $allowed[$entry] -ne $notice.sha256){throw 'Conflicting third-party notice hashes.'}
            if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing third-party notice: $entry"}
            if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $notice.sha256){throw "Changed third-party notice: $entry"}
            $allowed[$entry]=$notice.sha256
        }
    }
    return ,$allowed
}
foreach($required in @('app/CloudScribe.exe','app/CloudScribe.dll','app/appsettings.json','runtime/dotnet.exe','LICENSE','THIRD-PARTY-NOTICES.md','licenses/third-party/components.json')){
    if(-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)){throw "Incomplete clean package: $required"}
}
if(-not (Test-Path -LiteralPath (Join-Path $package 'runtime/shared/Microsoft.NETCore.App') -PathType Container)){throw 'Incomplete clean package: runtime/shared/Microsoft.NETCore.App'}
Assert-NoLinks $package
$all=@(Get-ChildItem -LiteralPath $package -Recurse -Force)
if($all | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Package contains links.'}
if($all | Where-Object {$_.Extension -in @('.db','.sqlite','.mp3','.wav','.flac','.utf8','.pdb','.jsonl','.log','.previous','.key','.pem','.pfx')}){throw 'Package contains private, diagnostic or debug files; use a clean package.'}
if((Get-FileHash -LiteralPath (Join-Path $package 'LICENSE') -Algorithm SHA256).Hash -ne 'a58a65454f49c01d25bed44828fe5e678592bbdfe20353c1f7dc7cc90c6cf1d3'){throw 'Unexpected project license identity.'}
$allowedNotices=Get-AllowedNoticeFiles
foreach($file in $all | Where-Object {-not $_.PSIsContainer}){
    if(-not $file.FullName.StartsWith($packagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Package entry escapes its root.'}
    $relative=$file.FullName.Substring($packagePrefix.Length).Replace('\','/')
    $isNoticeInventory=$relative -eq 'licenses/third-party/components.json'
    if($relative -notmatch '^(app|runtime)/' -and $relative -notmatch '^[^/]+\.(md|txt|cmd)$|^INSTALL-CLOUDSCRIBE\.ps1$' -and
        $relative -ne 'LICENSE' -and -not $isNoticeInventory -and -not $allowedNotices.ContainsKey($relative)){throw "Unexpected package entry: $relative"}
    if($file.Extension -eq '.json' -and -not $isNoticeInventory -and $file.Name -notin @('appsettings.json','CloudScribe.deps.json','CloudScribe.runtimeconfig.json','Microsoft.NETCore.App.deps.json','Microsoft.NETCore.App.runtimeconfig.json')){throw 'Unexpected JSON file; never install a credential-bearing package.'}
}
$settings=Get-Content -LiteralPath (Join-Path $package 'app/appsettings.json') -Raw | ConvertFrom-Json
if($settings.CloudScribe.AppDataDirectoryOverride){throw 'The source package must not contain a personal profile override.'}
if($ValidatePackageOnly){[pscustomobject]@{Valid=$true;PackageFiles=@($all | Where-Object {-not $_.PSIsContainer}).Count;NoticeFiles=$allowedNotices.Count;PackageOnly=$true};return}
$programs=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$target=Join-Path $programs 'CloudScribe Pro'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target)) -ne $programs){throw 'Install root escaped the Programs directory.'}
$menu=[Environment]::GetFolderPath('Programs')
$shortcut=Join-Path $menu 'CloudScribe Pro.lnk'
$shell=New-Object -ComObject WScript.Shell
if(Test-Path -LiteralPath $shortcut){
    $existing=$shell.CreateShortcut($shortcut)
    $expected=Join-Path $target 'app/CloudScribe.exe'
    if(-not [string]::Equals($existing.TargetPath,$expected,[StringComparison]::OrdinalIgnoreCase) -or $existing.Arguments){
        throw 'An existing CloudScribe Pro Start Menu shortcut points elsewhere or has custom arguments. It was preserved; inspect and rename it before installing.'
    }
}
Assert-NoLinks $target
$marker=Join-Path $target 'cloudscribe-install.json'
$profile=$null
if(Test-Path -LiteralPath $target){
    if(-not (Test-Path -LiteralPath $marker)){throw 'Existing installation is not managed by this installer; inspect it before replacing anything.'}
    $identity=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if($identity.Product -ne 'CloudScribe Pro'){throw 'Install identity mismatch.'}
    $oldSettings=Get-Content -LiteralPath (Join-Path $target 'app/appsettings.json') -Raw | ConvertFrom-Json
    $profile=$oldSettings.CloudScribe.AppDataDirectoryOverride
}
$migration=$null
if($MigrateProfileFrom){
    if($profile){throw 'A profile is already configured. Omit MigrateProfileFrom when updating.'}
    $migration=(Resolve-Path -LiteralPath $MigrateProfileFrom).Path
    Assert-NoLinks $migration
    if(-not (Test-Path -LiteralPath (Join-Path $migration 'data/cloudscribe.db'))){throw 'The migration source has no CloudScribe database.'}
    if(Get-ChildItem -LiteralPath $migration -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Migration source contains links.'}
    $profile=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CloudScribe Pro/Profiles/Studio'
    Assert-NoLinks $profile
    if(Test-Path -LiteralPath $profile){throw 'Migration destination already exists; no user data will be overwritten.'}
}
$reviewedRecovery=Get-ReviewedRecoveryPolicy $profile
$reviewedChirp=Get-ReviewedChirpPolicy $profile
$reviewedResume=Get-ReviewedChirpResumePolicy $profile
$completionGate=Get-ReviewedCompletionGate $profile
if($CompletePendingCompletionSuccessor){
    if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'Close CloudScribe normally before completing the exact pending transition.'}
    Complete-CompletionBuildInstall $completionGate (Join-Path $target 'app')
    [pscustomobject]@{CompletedPendingSuccessor=$true;NewReservation=$false;ProviderCalls=0;Executable=(Join-Path $target 'app/CloudScribe.exe')}
    return
}
$retainedCompletion=Get-RetainedCompletionAuthority $completionGate
if($completionGate -and $reviewedRecovery){throw 'An active completion gate does not authorize unrelated recovery-policy installation.'}
if($completionGate){
    foreach($authority in @($reviewedResume,$reviewedChirp)|Where-Object {$_}){
        $authorityRoot=[Text.Encoding]::UTF8.GetString($authority.Bytes)|ConvertFrom-Json
        $authorityBinding=if($authority -eq $reviewedResume){$authorityRoot.HistoricalPolicy}else{$authorityRoot}
        if([DateTimeOffset]$authorityRoot.ExpiresAtUtc -gt $completionGate.DeadlineUtc -or
            $authorityBinding.AccountId -cne $completionGate.Config.AccountId -or
            $authorityBinding.ProjectId -cne $completionGate.Config.ProjectId){throw 'Paid authority exceeds completion scope bindings/deadline.'}
    }
}
if($ValidateOnly){[pscustomobject]@{Valid=$true;PackageFiles=@($all | Where-Object {-not $_.PSIsContainer}).Count;InstallRoot=$target;MigrationRequested=[bool]$migration;ReviewedRecoveryPolicy=[bool]$reviewedRecovery;ReviewedChirpArticlePolicy=[bool]$reviewedChirp;ReviewedChirpResumePolicy=[bool]$reviewedResume;ReviewedCompletionGate=[bool]$completionGate};return}
if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'Close every CloudScribe window normally before installing/updating. No process will be killed.'}
$completionOperation=[Guid]::Empty;$completionIdentity=''
$buildTransition=if($completionGate){$completionGate.BuildTransition}else{$null}
$runSuccessor=if($completionGate){$completionGate.SuccessorTransition}else{$null}
if($runSuccessor){
    $null=Invoke-CompletionGate $completionGate 'claim-successor' $runSuccessor.Id $runSuccessor.Sha256 $null (Join-Path $package 'app')
    $null=Invoke-CompletionGate $completionGate 'check-successor' $runSuccessor.Id $runSuccessor.Sha256 $null (Join-Path $package 'app')
}
if($buildTransition){
    $null=Invoke-CompletionGate $completionGate 'claim-build' $buildTransition.Id $buildTransition.Sha256 $null (Join-Path $package 'app')
    $null=Invoke-CompletionGate $completionGate 'check-build' $buildTransition.Id $buildTransition.Sha256 $null (Join-Path $package 'app')
}

if($completionGate){
    if($reviewedResume -or $reviewedChirp){
        $completionOperation=[Guid]::NewGuid()
        $completionIdentity=if($reviewedResume){$reviewedResume.Sha256.ToLowerInvariant()}else{$reviewedChirp.Sha256.ToLowerInvariant()}
        $command=if($reviewedResume){'claim-resume'}else{'claim-completion'}
        $policyPath=if($reviewedResume){$ReviewedChirpResumePolicyPath}else{$ReviewedChirpArticlePolicyPath}
        $null=Invoke-CompletionGate $completionGate $command $completionOperation $completionIdentity $null (Join-Path $package 'app') $policyPath
        Assert-CompletionInstallEffect $completionGate $completionOperation $completionIdentity $reviewedResume
    }
    # Durable required state comes BEFORE activation/install effects. Interruption
    # leaves a fail-closed required marker; never silently remove or reset it.
    if(-not (Test-Path -LiteralPath $completionGate.RequiredPath)){
        Assert-NoLinks $completionGate.RequiredPath
        $requirement=@{GateSha256=$completionGate.Sha256;ScopeSha256=$completionGate.Config.ScopeSha256}|ConvertTo-Json -Compress
        $stream=[IO.FileStream]::new($completionGate.RequiredPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$bytes=[Text.Encoding]::UTF8.GetBytes($requirement);$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    }
}
[IO.Directory]::CreateDirectory($programs) | Out-Null
$suffix=[Guid]::NewGuid().ToString('N')
$stage=Join-Path $programs ('CloudScribe Pro.stage-'+$suffix)
$backup=Join-Path $programs ('CloudScribe Pro.previous-'+$suffix)
foreach($path in @($stage,$backup)){
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $programs){throw 'Install staging escaped the Programs directory.'}
    Assert-NoLinks $path
}
Copy-Item -LiteralPath $package -Destination $stage -Recurse
if($migration){
    [IO.Directory]::CreateDirectory((Split-Path -Parent $profile)) | Out-Null
    Copy-Item -LiteralPath $migration -Destination $profile -Recurse
    foreach($file in Get-ChildItem -LiteralPath $migration -Recurse -File){
        $copy=Join-Path $profile $file.FullName.Substring($migration.Length+1)
        if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash){throw 'Profile migration verification failed; original profile is intact. Installation has not switched.'}
    }
}
if($profile){
    $settings.CloudScribe.AppDataDirectoryOverride=$profile
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $stage 'app/appsettings.json') -Encoding utf8
}
$installMarker=@{Product='CloudScribe Pro';Version=1;Profile=$profile;AppSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app/CloudScribe.dll')).Hash;CompletionGateProtocolVersion=1}
if($reviewedRecovery){
    [IO.File]::WriteAllBytes((Join-Path $stage 'recovery-admission-policy.json'),$reviewedRecovery.Bytes)
    $installMarker.RecoveryPolicySha256=$reviewedRecovery.Sha256
}
if($reviewedChirp){
    [IO.File]::WriteAllBytes((Join-Path $stage 'chirp-article-admission-policy.json'),$reviewedChirp.Bytes)
    $installMarker.ChirpArticlePolicySha256=$reviewedChirp.Sha256
}
elseif($retainedCompletion){
    [IO.File]::WriteAllBytes((Join-Path $stage 'chirp-article-admission-policy.json'),$retainedCompletion.Bytes)
    $installMarker.ChirpArticlePolicySha256=$retainedCompletion.Sha256
}
if($reviewedResume){
    [IO.File]::WriteAllBytes((Join-Path $stage 'chirp-reservation-resume-admission.json'),$reviewedResume.Bytes)
    $installMarker.ChirpReservationResumePolicySha256=$reviewedResume.Sha256
}
if($completionGate){
    [IO.File]::WriteAllBytes((Join-Path $stage 'completion-workflow-gate.json'),$completionGate.Bytes)
    $installMarker.CompletionWorkflowGateSha256=$completionGate.Sha256
}
# Ordinary updates never copy private recovery authority. The explicit active-run
# zero-funds resume above retains ONLY its already-admitted exact article policy,
# with register proof; it cannot renew expiry, budget or attempts.
$installMarker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'cloudscribe-install.json') -Encoding utf8
$hadPrevious=Test-Path -LiteralPath $target
if($completionGate -and $completionOperation -ne [Guid]::Empty){Assert-CompletionInstallEffect $completionGate $completionOperation $completionIdentity $reviewedResume}
if($buildTransition){
    if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'CloudScribe reopened during build succession; preserve pending state and stop.'}
    foreach($pair in @(@('CloudScribe.dll',$completionGate.Config.AppSha256),@('CloudScribe.Infrastructure.dll',$completionGate.Config.InfrastructureSha256))){
        if((Get-FileHash -LiteralPath (Join-Path $stage ('app/'+$pair[0])) -Algorithm SHA256).Hash -ine $pair[1]){throw 'Staged successor differs; preserve the pending transition.'}
    }
    $null=Invoke-CompletionGate $completionGate 'check-build' $buildTransition.Id $buildTransition.Sha256 $null (Join-Path $package 'app')
}
if($runSuccessor){
    if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'CloudScribe reopened during successor-run installation; preserve pending state.'}
    foreach($pair in @(@('CloudScribe.dll',$completionGate.Config.AppSha256),@('CloudScribe.Infrastructure.dll',$completionGate.Config.InfrastructureSha256))){
        if((Get-FileHash -LiteralPath (Join-Path $stage ('app/'+$pair[0]))).Hash -ine $pair[1]){throw 'Staged successor-run build differs; preserve pending state.'}
    }
    $null=Invoke-CompletionGate $completionGate 'check-successor' $runSuccessor.Id $runSuccessor.Sha256 $null (Join-Path $package 'app')
}
if($hadPrevious){Move-Item -LiteralPath $target -Destination $backup}
try{Move-Item -LiteralPath $stage -Destination $target}catch{if($hadPrevious -and -not (Test-Path -LiteralPath $target)){Move-Item -LiteralPath $backup -Destination $target};throw}
if($buildTransition -or $runSuccessor){
    Complete-CompletionBuildInstall $completionGate (Join-Path $target 'app')
}
$link=$shell.CreateShortcut($shortcut)
$link.TargetPath=Join-Path $target 'app/CloudScribe.exe'
$link.WorkingDirectory=Join-Path $target 'app'
$link.Arguments=''
$link.IconLocation=$link.TargetPath+',0'
$link.Description='CloudScribe Pro - text to MP3'
$link.Save()
if($completionGate -and $completionOperation -ne [Guid]::Empty){
    $actualMarker=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
    if($actualMarker.CompletionWorkflowGateSha256 -cne $completionGate.Sha256){throw 'Installed completion gate pin differs; keep pending state.'}
    $actualPolicy=if($reviewedResume){Join-Path $target 'chirp-reservation-resume-admission.json'}else{Join-Path $target 'chirp-article-admission-policy.json'}
    if((Get-FileHash -LiteralPath $actualPolicy -Algorithm SHA256).Hash.ToLowerInvariant() -cne $completionIdentity){throw 'Installed authority differs; keep pending state.'}
    $receipt=@{id=$completionOperation.ToString('D');identitySha256=$completionIdentity;outcome='Succeeded';evidenceSha256=(Get-FileHash -LiteralPath (Join-Path $target 'cloudscribe-install.json')).Hash.ToLowerInvariant();policyVerified=$true}
    if($reviewedResume){$receipt.zeroNewFunds=$true}
    else{
        $installedArticle=Get-Content -LiteralPath $actualPolicy -Raw|ConvertFrom-Json
        $receipt.maximumNewAttempts=[int]$installedArticle.MaximumNewAttempts
        $receipt.additionalReservationCents=[long]$installedArticle.MaximumAdditionalReservationMinorUnits
    }
    $installedGate=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$completionGate.Sha256;Config=$completionGate.Config}
    $null=Invoke-CompletionGate $installedGate 'complete' $completionOperation $completionIdentity $receipt (Join-Path $target 'app')
}
[pscustomobject]@{Executable=$link.TargetPath;Shortcut=$shortcut;Profile=$profile;PreviousInstall=$(if($hadPrevious){$backup}else{$null});Taskbar='Right-click the running CloudScribe gold-star icon and choose Pin to taskbar. Windows controls pinning.'}
