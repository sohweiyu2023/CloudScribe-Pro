[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath,[switch]$ExpiredWindow)
$ErrorActionPreference='Stop'
Write-Output 'Synthetic diagnostic stage: setup (no live profile or provider).'
# Inert caller-owned temporary package/profile only. No production install, credentials or provider requests.
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$root=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-diagnostic-installer-'+[Guid]::NewGuid().ToString('N'))
$arguments=@('-I','-S',(Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_sentence_diagnostic_fixture.py'),$root)
if($ExpiredWindow){$arguments+='expired'}
$metadata=& $PythonPath @arguments
if($LASTEXITCODE -ne 0){throw 'Synthetic diagnostic fixture setup failed.'}
$f=$metadata|ConvertFrom-Json
if($f.syntheticOnly -ne $true -or $f.providerCalls -ne 0 -or $f.credentialReads -ne 0){throw 'The fixture is not isolated.'}
foreach($path in @($f.package,$f.target,$f.profile,$f.gatePath,$f.handoffPath)){
    if(-not [IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($root)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic fixture escaped its root.'}
}
$package=$f.package;$target=$f.target;$profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath;$ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionSuccessorRunPath=$f.handoffPath;$ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$ReviewedCompletionBuildTransitionPath=$null;$ReviewedCompletionBuildTransitionSha256=$null
$ReviewedChirpResumePolicyPath=$null;$ReviewedChirpArticlePolicyPath=$null;$MigrateProfileFrom=$null
$CompletePendingCompletionSuccessor=$false;$CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
function Get-Process {param($Name,$ErrorAction)if($Name -cne 'CloudScribe'){throw 'Unexpected process inspection.'};return $null}
Write-Output 'Synthetic diagnostic stage: inspect.'
$gate=Get-ReviewedCompletionGate $profile
if(Test-Path -LiteralPath $gate.Config.RegisterPath){throw 'Read-only inspection created an executable register.'}
if([DateTimeOffset]$gate.DeadlineUtc -ne [DateTimeOffset]$f.deadlineUtc){throw 'The reader extended or changed the reviewed cutoff.'}
$h=$gate.SuccessorTransition
if($h.Record.SchemaVersion -ne $(if($ExpiredWindow){6}else{5})){throw 'Unexpected diagnostic handoff type.'}
$before=(Get-FileHash -LiteralPath $f.databasePath -Algorithm SHA256).Hash
Write-Output 'Synthetic diagnostic stage: claim-successor.'
$null=Invoke-CompletionGate $gate 'claim-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')
$null=Invoke-CompletionGate $gate 'check-successor' $h.Id $h.Sha256 $null (Join-Path $package 'app')
# Simulate only the normal install's reviewed metadata switch over inert app bytes.
Copy-Item -LiteralPath $gate.Path -Destination (Join-Path $target 'completion-workflow-gate.json') -Force
$marker=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$marker.CompletionWorkflowGateSha256=$gate.Sha256
[IO.File]::WriteAllText((Join-Path $target 'cloudscribe-install.json'),($marker|ConvertTo-Json -Depth 10 -Compress))
Write-Output 'Synthetic diagnostic stage: reject retained predecessor financial authority.'
$rejected=$false
try{Complete-CompletionBuildInstall $gate (Join-Path $target 'app')}catch{
    if($_.Exception.Message -notlike '*TransitionFinancialAdmissionExists*'){throw}
    $rejected=$true
}
if(-not $rejected){throw 'Predecessor financial authority was carried into a diagnostic installation.'}
# Reconcile the same synthetic pending transition after the known metadata error;
# preserve all database rows and holds, and never create a new operation.
foreach($name in @('ChirpArticlePolicySha256','ChirpReservationResumePolicySha256')){$marker.PSObject.Properties.Remove($name)}
[IO.File]::WriteAllText((Join-Path $target 'cloudscribe-install.json'),($marker|ConvertTo-Json -Depth 10 -Compress))
$CompletePendingCompletionSuccessor=$true
Write-Output 'Synthetic diagnostic stage: complete same reviewed pending transition.'
Complete-CompletionBuildInstall $gate (Join-Path $target 'app')
$CompletePendingCompletionSuccessor=$false
$installed=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config;SuccessorTransition=$h}
$status=Invoke-CompletionGate $installed 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne $f.expectedRefreshes -or $status.totalAdmissions -ne $f.expectedAdmissions -or $status.pendingId){throw 'Diagnostic transition reset counters or created an admission.'}
if($before -cne (Get-FileHash -LiteralPath $f.databasePath -Algorithm SHA256).Hash){throw 'Diagnostic transition changed synthetic documents or finances.'}
$rejected=$false
try{Complete-CompletionBuildInstall $gate (Join-Path $target 'app')}catch{$rejected=$true}
if(-not $rejected){throw 'A committed diagnostic transition replay was accepted.'}
Write-Output 'Actual diagnostic installer reader/writer PASS: preserved database, counters and cutoff; no paid admission or provider calls.'
