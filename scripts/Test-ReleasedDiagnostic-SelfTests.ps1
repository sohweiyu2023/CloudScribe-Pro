[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath, [string]$MetadataPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
if(-not $MetadataPath){
    $temporary=& $PythonPath -I -S -c 'import tempfile; print(tempfile.gettempdir())'
    if($LASTEXITCODE -ne 0 -or @($temporary).Count -ne 1){throw 'Synthetic temporary root unavailable.'}
    $root=Join-Path ([string]$temporary) ('cloudscribe-post-release-installer-'+[Guid]::NewGuid().ToString('N'))
    Write-Output 'Synthetic post-release installer: preparing sealed history and inert profile; no provider.'
    $result=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_released_diagnostic_fixture.py') $root
    if($LASTEXITCODE -ne 0){throw 'Synthetic post-release fixture setup failed.'}
    $f=$result|ConvertFrom-Json
    $MetadataPath=Join-Path $root 'metadata.json'
    $f|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $MetadataPath -Encoding utf8
}else{
    $f=Get-Content -LiteralPath $MetadataPath -Raw|ConvertFrom-Json
    $root=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($MetadataPath))
}
if($f.syntheticOnly -ne $true -or $f.providerCalls -ne 0 -or $f.credentialReads -ne 0){throw 'Non-synthetic metadata refused.'}
foreach($path in @($f.package,$f.target,$f.profile,$f.gatePath,$f.handoffPath,$f.databasePath)){
    Assert-NoLinks $path
    if(-not [IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($root)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic fixture escaped its root.'}
}
$package=$f.package; $target=$f.target; $profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath; $ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionSuccessorRunPath=$f.handoffPath; $ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$ReviewedCompletionBuildTransitionPath=$null; $ReviewedCompletionBuildTransitionSha256=$null
$ReviewedRecoveryPolicyPath=$null; $ReviewedChirpResumePolicyPath=$null; $ReviewedChirpArticlePolicyPath=$null
$CompletePendingCompletionSuccessor=$false; $CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
function Get-Process {param($Name,$ErrorAction) if($Name -cne 'CloudScribe'){throw 'Unexpected process inspection.'};return $null}
$databaseBefore=(Get-FileHash -LiteralPath $f.databasePath).Hash
$archiveBefore=(Get-FileHash -LiteralPath $f.requiredArchive).Hash
Write-Output 'Synthetic post-release installer: exact manual inspection.'
$gate=Get-ReviewedCompletionGate $profile
if(Test-Path -LiteralPath $gate.Config.RegisterPath){throw 'Inspection created a child register.'}
if($gate.SuccessorTransition.Record.SchemaVersion -ne 8){throw 'Wrong transition contract.'}
$ReviewedChirpArticlePolicyPath=$f.policyPath
$rejected=$false
try{Get-ReviewedCompletionGate $profile|Out-Null}catch{if($_.Exception.Message -notlike '*cannot include financial policy admission*'){throw};$rejected=$true}
if(-not $rejected){throw 'Activation accepted combined financial authority.'}
$ReviewedChirpArticlePolicyPath=$null
$h=$gate.SuccessorTransition
Write-Output 'Synthetic post-release installer: claim/check, then interrupt before installed metadata.'
$null=Invoke-CompletionGate $gate 'claim-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')
$null=Invoke-CompletionGate $gate 'check-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')
$requirement=@{GateSha256=$gate.Sha256;ScopeSha256=$gate.Config.ScopeSha256}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($gate.RequiredPath,$requirement)
$null=Invoke-CompletionGate $gate 'check-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')
$rejected=$false
try{Complete-CompletionBuildInstall $gate (Join-Path $target 'app')}catch{$rejected=$true}
if(-not $rejected){throw 'Incomplete installed metadata was certified.'}
# Only inert fixture metadata is switched; no executable is launched or real profile touched.
Copy-Item -LiteralPath $gate.Path -Destination (Join-Path $target 'completion-workflow-gate.json')
$marker=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$marker|Add-Member -NotePropertyName CompletionGateProtocolVersion -NotePropertyValue 1 -Force
$marker|Add-Member -NotePropertyName CompletionWorkflowGateSha256 -NotePropertyValue $gate.Sha256 -Force
$marker|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Encoding utf8
Write-Output 'Synthetic post-release installer: finish the same transition and verify readback.'
Complete-CompletionBuildInstall $gate (Join-Path $target 'app')
$oldRegister=Get-Content -LiteralPath $h.Record.PreviousConfigPath -Raw|ConvertFrom-Json
$beforeOld=(Get-FileHash -LiteralPath $oldRegister.RegisterPath).Hash
$beforeNew=(Get-FileHash -LiteralPath $gate.Config.RegisterPath).Hash
Complete-CompletionBuildInstall $gate (Join-Path $target 'app')
if($beforeOld -cne (Get-FileHash -LiteralPath $oldRegister.RegisterPath).Hash -or $beforeNew -cne (Get-FileHash -LiteralPath $gate.Config.RegisterPath).Hash){throw 'Completed-result readback appended authority.'}
$installed=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config;SuccessorTransition=$h}
$status=Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne $f.expectedRefreshes -or $status.totalAdmissions -ne $f.expectedAdmissions -or $status.resumeAdmissions -ne $f.expectedResumeAdmissions -or $status.pendingId){throw 'Counters or pending state differ.'}
if($databaseBefore -cne (Get-FileHash -LiteralPath $f.databasePath).Hash -or $archiveBefore -cne (Get-FileHash -LiteralPath $f.requiredArchive).Hash){throw 'Synthetic financial/media database or historical archive changed.'}
Write-Output 'Synthetic post-release installer: independently pinned one-request policy admission in the same inert fixture.'
$metadataSha=(Get-FileHash -LiteralPath $MetadataPath).Hash.ToLowerInvariant()
$admission=& $PythonPath -I -S -B (Join-Path $PSScriptRoot '../tools/completion_workflow/verify_synthetic_released_admission.py') $MetadataPath $metadataSha
if($LASTEXITCODE -ne 0){$admission|Write-Output;throw 'Synthetic separately reviewed admission verification failed.'}
$admission|Write-Output
[pscustomobject]@{Passed=$true;SyntheticOnly=$true;ProviderCalls=0;NewReservations=0;LiveFinancialAdmission=$false;SyntheticPolicyAdmissionVerified=$true;MetadataPath=$MetadataPath;MetadataSha256=$metadataSha;DeadlineUtc=$gate.DeadlineUtc;HistoricalCountersPreserved=$true;InterruptedMetadataRejected=$true;SameTransitionFinished=$true;CompletedReadbackAppendsNothing=$true}
