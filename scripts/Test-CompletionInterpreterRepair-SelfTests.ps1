[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath)
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failure.'}
foreach($name in @('Assert-NoLinks','Assert-UniqueRecoveryJson','Assert-RecoveryRecord','Get-ReviewedCompletionSuccessorRun','Assert-CompletionInterpreterIdentity','Set-CompletionInterpreterPins','Restore-CompletionInterpreterPins','Wait-CompletionBridgeProcess','Invoke-CompletionGate','Get-ReviewedCompletionGate','Complete-CompletionBuildInstall')){
    $functions=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($functions.Count -ne 1){throw 'Missing production function.'};Invoke-Expression $functions[0].Extent.Text
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-interpreter-tests-'+[Guid]::NewGuid().ToString('N'))
$metadata=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_interpreter_fixture.py') $root
if($LASTEXITCODE -ne 0){throw 'Synthetic setup failed.'}
$f=$metadata|ConvertFrom-Json
$package=$f.package;$target=$f.target;$profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath;$ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionBuildTransitionPath=$f.transitionPath;$ReviewedCompletionBuildTransitionSha256=$f.transitionSha256
$ReviewedCompletionSuccessorRunPath=$null;$ReviewedCompletionSuccessorRunSha256=$null
$ReviewedChirpResumePolicyPath=$null;$ReviewedChirpArticlePolicyPath=$null;$MigrateProfileFrom=$null
$CompletePendingCompletionSuccessor=$false;$CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$passed=0
function Expect-Rejected([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Expected rejection.'};$script:passed++}
# Only signature response and app-liveness are synthetic; no real certificate,
# credential store, process termination or full installer entrypoint is used.
$signatureStatus='Valid';$signatureSubject='CN=Python Software Foundation, O=Synthetic';$signatureThumb='A'*40
function Get-AuthenticodeSignature {param($LiteralPath)[pscustomobject]@{Status=$script:signatureStatus;SignerCertificate=[pscustomobject]@{Subject=$script:signatureSubject;Thumbprint=$script:signatureThumb}}}
function Get-Process {param($Name,$ErrorAction)if($Name -cne 'CloudScribe'){throw 'Unexpected process inspection.'};return $null}
$signatureStatus='NotSigned';Expect-Rejected {Get-ReviewedCompletionGate $profile};$signatureStatus='Valid'
$signatureSubject='CN=Unexpected Publisher';Expect-Rejected {Get-ReviewedCompletionGate $profile};$signatureSubject='CN=Python Software Foundation, O=Synthetic'
$signatureThumb='B'*40;Expect-Rejected {Get-ReviewedCompletionGate $profile};$signatureThumb='A'*40
$gate=Get-ReviewedCompletionGate $profile;$passed++
$operation=$gate.BuildTransition.Id;$pin=$gate.BuildTransition.Sha256
$null=Invoke-CompletionGate $gate 'claim-build' $operation $pin $null (Join-Path $package 'app');$passed++
Expect-Rejected {Invoke-CompletionGate $gate 'claim-build' $operation $pin $null (Join-Path $package 'app')}
# A partial audit backup must fail before changing ANY installed pin.
$backup=$target+'.interpreter-repair-'+$operation.ToString('N');[IO.Directory]::CreateDirectory($backup)|Out-Null
Copy-Item -LiteralPath (Join-Path $target 'completion-workflow-gate.json') -Destination (Join-Path $backup 'previous-gate.json')
Expect-Rejected {Set-CompletionInterpreterPins $gate (Join-Path $target 'app')}
if((Get-FileHash -LiteralPath (Join-Path $target 'completion-workflow-gate.json')).Hash -ine $f.previousGateSha256){throw 'Partial backup changed live fixture gate.'};$passed++
# Explicit fixture reconciliation supplies the missing predecessor copies.
Copy-Item -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Destination (Join-Path $backup 'previous-install.json')
Copy-Item -LiteralPath (Join-Path $profile '.completion-workflow-required.json') -Destination (Join-Path $backup 'previous-required.json')
Set-CompletionInterpreterPins $gate (Join-Path $target 'app');$passed++
# Restart after the first two files switched: only the exact pending repair is accepted.
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
Expect-Rejected {Get-ReviewedCompletionGate $profile}
$CompletePendingCompletionInterpreterRepair=$true
$gate=Get-ReviewedCompletionGate $profile;$passed++
Set-CompletionInterpreterPins $gate (Join-Path $target 'app');$passed++
Complete-CompletionBuildInstall $gate (Join-Path $target 'app');$passed++
Expect-Rejected {Complete-CompletionBuildInstall $gate (Join-Path $target 'app')}
# Runtime bridge verifies repaired config plus full ancestor chain, counters unchanged.
$installed=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config}
$status=Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne 0 -or $status.totalAdmissions -ne 0 -or $status.pendingId){throw 'Repair changed operation counters.'};$passed++
$withdrawalPath=Join-Path $root 'synthetic-withdrawal.json'
$metadata=& $PythonPath -I -S (Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_interpreter_withdrawal.py') $root $f.gatePath $f.transitionPath $backup $withdrawalPath
if($LASTEXITCODE -ne 0){throw 'Synthetic withdrawal setup failed.'}
$withdrawal=$metadata|ConvertFrom-Json
$gate|Add-Member -NotePropertyName Withdrawal -NotePropertyValue $withdrawal
$id=[Guid]$withdrawal.Record.WithdrawalId
$null=Invoke-CompletionGate $gate 'inspect-interpreter-withdrawal' $id $withdrawal.Sha256 $null (Join-Path $target 'app');$passed++
$null=Invoke-CompletionGate $gate 'claim-interpreter-withdrawal' $id $withdrawal.Sha256 $null (Join-Path $target 'app');$passed++
Expect-Rejected {Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')}
Restore-CompletionInterpreterPins $gate;$passed++
foreach($entry in @(@('completion-workflow-gate.json','previous-gate.json'),@('cloudscribe-install.json','previous-install.json'))){
    if((Get-FileHash -LiteralPath (Join-Path $target $entry[0])).Hash -cne (Get-FileHash -LiteralPath (Join-Path $backup $entry[1])).Hash){throw 'Withdrawal did not restore exact original bytes.'};$passed++
}
if((Get-FileHash -LiteralPath (Join-Path $profile '.completion-workflow-required.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $backup 'previous-required.json')).Hash){throw 'Withdrawal did not restore required marker.'};$passed++
Expect-Rejected {Restore-CompletionInterpreterPins $gate}
[pscustomobject]@{Passed=$passed;SignatureMocked=$true;ProviderCalls=0;CredentialOperations=0;RealInstalls=0;FixtureRoot=$root}
