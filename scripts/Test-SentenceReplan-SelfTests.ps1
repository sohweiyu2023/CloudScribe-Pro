[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath,[switch]$AfterReboot,[switch]$ExpiredTerminal)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$root=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-sentence-tests-'+[Guid]::NewGuid().ToString('N'))
if($AfterReboot -and $ExpiredTerminal){throw 'Choose exactly one synthetic transition mode.'}
$fixtureArgs=@($root);if($AfterReboot){$fixtureArgs+='after-reboot'}elseif($ExpiredTerminal){$fixtureArgs+='expired-terminal'}
$metadata=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_sentence_fixture.py') @fixtureArgs
if($LASTEXITCODE -ne 0){throw 'Synthetic same-window setup failed.'};$f=$metadata|ConvertFrom-Json
$package=$f.package;$target=$f.target;$profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath;$ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionSuccessorRunPath=$f.handoffPath;$ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$ReviewedCompletionBuildTransitionPath=$null;$ReviewedCompletionBuildTransitionSha256=$null
$ReviewedChirpResumePolicyPath=$null;$ReviewedChirpArticlePolicyPath=$null;$MigrateProfileFrom=$null
$CompletePendingCompletionSuccessor=$false;$CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$passed=0
function Expect-Rejected([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Expected rejection.'};$script:passed++}
$saved=[IO.File]::ReadAllBytes($ReviewedCompletionSuccessorRunPath)
$mutations=@('counter','kind','unknown','failed-size','failed-zero');if($AfterReboot -or $ExpiredTerminal){$mutations+=@('clock-unknown','clock-boolean')};if($ExpiredTerminal){$mutations+=@('window-approval','missing-window-approval')}
foreach($mutation in $mutations){
    $record=[Text.Encoding]::UTF8.GetString($saved)|ConvertFrom-Json
    switch($mutation){
        'counter' {$record.UsedAdmissions=0}
        'kind' {$record.TransitionKind='whole-article-interpreter-continuation'}
        'unknown' {$record|Add-Member -NotePropertyName AutoApprove -NotePropertyValue $true}
        'failed-size' {$record.FailedPayloadBytes=5001}
        'failed-zero' {$record.FailedLocalAttemptId='0'*32}
        'clock-unknown' {$record.ClockEpochEvidence|Add-Member -NotePropertyName AutoApprove -NotePropertyValue $true}
        'clock-boolean' {$record.ClockEpochEvidence.ObservedMonoMicros=$true}
        'window-approval' {$record.WindowApprovalSha256='0'*64}
        'missing-window-approval' {$record.PSObject.Properties.Remove('WindowApprovalSha256')}
    }
    [IO.File]::WriteAllText($ReviewedCompletionSuccessorRunPath,($record|ConvertTo-Json -Depth 20 -Compress))
    $ReviewedCompletionSuccessorRunSha256=(Get-FileHash -LiteralPath $ReviewedCompletionSuccessorRunPath).Hash.ToLowerInvariant()
    Expect-Rejected {Get-ReviewedCompletionGate $profile}
}
[IO.File]::WriteAllBytes($ReviewedCompletionSuccessorRunPath,$saved);$ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$gate=Get-ReviewedCompletionGate $profile;$passed++
if(Test-Path -LiteralPath $gate.Config.RegisterPath){throw 'Inspection created successor authority.'};$passed++
$backup=Join-Path $root 'control-backup';[IO.Directory]::CreateDirectory($backup)|Out-Null
$entries=@(@((Join-Path $target 'completion-workflow-gate.json'),'gate.json'),@((Join-Path $target 'cloudscribe-install.json'),'install.json'),@((Join-Path $profile '.completion-workflow-required.json'),'required.json'))
foreach($entry in $entries){Copy-Item -LiteralPath $entry[0] -Destination (Join-Path $backup $entry[1])}
$h=$gate.SuccessorTransition
$null=Invoke-CompletionGate $gate 'claim-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app');$passed++
$null=Invoke-CompletionGate $gate 'check-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app');$passed++
Copy-Item -LiteralPath $gate.Path -Destination (Join-Path $target 'completion-workflow-gate.json') -Force
$marker=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$marker.CompletionWorkflowGateSha256=$gate.Sha256
foreach($name in @('ChirpArticlePolicySha256','ChirpReservationResumePolicySha256')){$marker.PSObject.Properties.Remove($name)}
[IO.File]::WriteAllText((Join-Path $target 'cloudscribe-install.json'),($marker|ConvertTo-Json -Depth 10 -Compress))
Complete-CompletionBuildInstall $gate (Join-Path $target 'app');$passed++
$installed=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config;SuccessorTransition=$h}
$status=Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne $f.expectedRefreshes -or $status.totalAdmissions -ne $f.expectedAdmissions -or $status.pendingId -or [DateTimeOffset]$status.deadlineUtc -ne [DateTimeOffset]$f.deadlineUtc){throw 'Counters/window changed across actual writer/readback.'};$passed++
if((Get-FileHash -LiteralPath $f.databasePath).Hash.ToLowerInvariant() -cne $f.databaseSha256){throw 'Synthetic financial database changed.'};$passed++
Expect-Rejected {Invoke-CompletionGate $installed 'claim-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')}
# Restoring old controls preserves bytes but MUST NOT resurrect the retired paid scope.
foreach($entry in $entries){Copy-Item -LiteralPath (Join-Path $backup $entry[1]) -Destination $entry[0] -Force}
$oldConfig=Get-Content -LiteralPath $f.previousGatePath -Raw|ConvertFrom-Json
$oldGate=[pscustomobject]@{Path=$f.previousGatePath;Sha256=$f.previousGateSha256;Config=$oldConfig}
Expect-Rejected {Invoke-CompletionGate $oldGate 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')}
foreach($entry in $entries){if((Get-FileHash -LiteralPath $entry[0]).Hash -cne (Get-FileHash -LiteralPath (Join-Path $backup $entry[1])).Hash){throw 'Control rollback differs.'}};$passed++
[pscustomobject]@{Passed=$passed;ProviderCalls=0;CredentialOperations=0;RealInstalls=0;CountersPreserved=$true;DeadlinePreserved=$true;RollbackDoesNotReactivateOldScope=$true;FixtureRoot=$root}
