[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$root=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-interpreter-tests-'+[Guid]::NewGuid().ToString('N'))
$metadata=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_expired_interpreter_fixture.py') $root
if($LASTEXITCODE -ne 0){throw 'Synthetic expired-scope setup failed.'}
$f=$metadata|ConvertFrom-Json
$package=$f.package;$target=$f.target;$profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath;$ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionSuccessorRunPath=$f.handoffPath;$ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$ReviewedCompletionBuildTransitionPath=$null;$ReviewedCompletionBuildTransitionSha256=$null
$ReviewedChirpResumePolicyPath=$null;$ReviewedChirpArticlePolicyPath=$null;$MigrateProfileFrom=$null
$CompletePendingCompletionSuccessor=$false;$CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$passed=0
function Expect-Rejected([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Expected rejection.'};$script:passed++}
$signatureStatus='Valid'
function Get-AuthenticodeSignature {param($LiteralPath)[pscustomobject]@{Status=$script:signatureStatus;SignerCertificate=[pscustomobject]@{Subject='CN=Python Software Foundation, O=Synthetic';Thumbprint=('A'*40)}}}
function Get-Process {param($Name,$ErrorAction)if($Name -cne 'CloudScribe'){throw 'Unexpected process inspection.'};return $null}
$signatureStatus='NotSigned';Expect-Rejected {Get-ReviewedCompletionGate $profile};$signatureStatus='Valid'
$gate=Get-ReviewedCompletionGate $profile;$passed++
if(Test-Path -LiteralPath $gate.Config.RegisterPath){throw 'Inspection created a fresh register.'};$passed++
$h=$gate.SuccessorTransition
$null=Invoke-CompletionGate $gate 'claim-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app');$passed++
Save-CompletionSuccessorInterpreterPins $gate;$passed++
$backup=$target+'.interpreter-repair-'+$h.Id.ToString('N')
# Actual production writer/readback: switch reviewed inert install metadata;
# app bytes remain identical, then commit required-marker and activation.
Copy-Item -LiteralPath $gate.Path -Destination (Join-Path $target 'completion-workflow-gate.json') -Force
$marker=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$marker.CompletionWorkflowGateSha256=$gate.Sha256
[IO.File]::WriteAllText((Join-Path $target 'cloudscribe-install.json'),($marker|ConvertTo-Json -Depth 10 -Compress))
Complete-CompletionBuildInstall $gate (Join-Path $target 'app');$passed++
$installed=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config;SuccessorTransition=$h}
$status=Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne 0 -or $status.totalAdmissions -ne 0 -or $status.pendingId){throw 'Activation created paid permission.'};$passed++
# Separate safety-only withdrawal restores old scope controls while BOTH old and
# new scopes remain unable to dispatch. No old interpreter is executed.
$metadata=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_interpreter_withdrawal.py') $root $f.gatePath $f.handoffPath $backup (Join-Path $root 'withdrawal.json')
if($LASTEXITCODE -ne 0){throw 'Synthetic successor withdrawal setup failed.'}
$withdrawal=$metadata|ConvertFrom-Json
$gate|Add-Member -NotePropertyName Withdrawal -NotePropertyValue $withdrawal
$gate.BuildTransition=[pscustomobject]@{Path=$h.Path;Sha256=$h.Sha256;Id=$h.Id;Kind=$h.Kind;Record=$h.Record}
$wid=[Guid]$withdrawal.Record.WithdrawalId
$null=Invoke-CompletionGate $gate 'inspect-interpreter-withdrawal' $wid $withdrawal.Sha256 $null (Join-Path $target 'app');$passed++
$null=Invoke-CompletionGate $gate 'claim-interpreter-withdrawal' $wid $withdrawal.Sha256 $null (Join-Path $target 'app');$passed++
Restore-CompletionInterpreterPins $gate;$passed++
foreach($pair in @(@((Join-Path $target 'completion-workflow-gate.json'),'previous-gate.json'),@((Join-Path $target 'cloudscribe-install.json'),'previous-install.json'),@((Join-Path $profile '.completion-workflow-required.json'),'previous-required.json'))){
    if((Get-FileHash -LiteralPath $pair[0]).Hash -cne (Get-FileHash -LiteralPath (Join-Path $backup $pair[1])).Hash){throw 'Exact predecessor restoration failed.'};$passed++
}
Expect-Rejected {Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')}
[pscustomobject]@{Passed=$passed;SignatureMocked=$true;ProviderCalls=0;CredentialOperations=0;RealInstalls=0;OldInterpreterExecuted=$false;FixtureRoot=$root}
