[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath)
$ErrorActionPreference='Stop'
$installerPath=Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installerPath,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer parse failure.'}
foreach($name in @('Assert-NoLinks','Assert-UniqueRecoveryJson','Invoke-CompletionGate','Get-ReviewedCompletionGate','Assert-CompletionInstallEffect','Complete-CompletionBuildInstall')){
    $found=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($found.Count -ne 1){throw 'Expected one production installer function.'}
    Invoke-Expression $found[0].Extent.Text
}
$fixtureScript=Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_gate_fixture.py'
$fixtureParent=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-completion-gate-tests-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureParent)|Out-Null
$passed=0
function New-GateFixture {
    $root=Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -cne [IO.Path]::GetFullPath($fixtureParent)){throw 'Fixture escaped its root.'}
    $metadata=& $PythonPath -I -S $fixtureScript $root
    if($LASTEXITCODE -ne 0){throw 'Synthetic setup failed.'}
    return ($metadata|ConvertFrom-Json)
}
function Bind-GateFixture($fixture){
    $script:package=$fixture.package;$script:target=$fixture.target;$script:profile=$fixture.profile
    $script:ReviewedCompletionGatePath=$fixture.gatePath;$script:ReviewedCompletionGateSha256=$fixture.gateSha256
    $script:ReviewedChirpResumePolicyPath=$null;$script:ReviewedChirpArticlePolicyPath=$null;$script:MigrateProfileFrom=$null;$script:identity=[pscustomobject]@{CompletionGateProtocolVersion=1}
    $script:ReviewedCompletionBuildTransitionPath=$null;$script:ReviewedCompletionBuildTransitionSha256=$null
}
function Expect-Rejected([scriptblock]$action){
    $rejected=$false;try{& $action|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Expected production guard rejection.'}
    $script:passed++
}

$fixture=New-GateFixture;Bind-GateFixture $fixture
$gate=Get-ReviewedCompletionGate $profile
if(-not $gate){throw 'Reviewed synthetic gate was not read.'};$passed++
$operation=[Guid]::NewGuid();$operationIdentity=$fixture.resumeSha256
$null=Invoke-CompletionGate $gate 'claim-resume' $operation $operationIdentity $null (Join-Path $package 'app') $fixture.resumePath;$passed++
Assert-CompletionInstallEffect $gate $operation $operationIdentity $null;$passed++
# Simulated interruption after durable claim, before installer mutation. A NEW
# bridge process sees the same pending slot; neither inspection nor claim retries.
Expect-Rejected {Get-ReviewedCompletionGate $profile}
Expect-Rejected {Invoke-CompletionGate $gate 'claim-resume' ([Guid]::NewGuid()) $operationIdentity $null (Join-Path $package 'app') $fixture.resumePath}
$status=Invoke-CompletionGate $gate 'status' ([Guid]::Empty) '' $null (Join-Path $package 'app')
if($status.resumeAdmissions -ne 1 -or $status.pendingId -cne $operation.ToString('D')){throw 'Interrupted admission was forgotten.'};$passed++

$fixture=New-GateFixture;Bind-GateFixture $fixture
$ReviewedCompletionGateSha256='b'*64;Expect-Rejected {Get-ReviewedCompletionGate $profile}
Bind-GateFixture $fixture
[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.dll'),'tampered inert fixture')
Expect-Rejected {Get-ReviewedCompletionGate $profile}

$fixture=New-GateFixture;Bind-GateFixture $fixture
$requiredPath=Join-Path $profile '.completion-workflow-required.json'
@{GateSha256=('c'*64);ScopeSha256=$fixture.scopeSha256}|ConvertTo-Json|Set-Content -LiteralPath $requiredPath
Expect-Rejected {Get-ReviewedCompletionGate $profile}
Bind-GateFixture $fixture;$ReviewedCompletionGatePath=$null;$ReviewedCompletionGateSha256=$null
Expect-Rejected {Get-ReviewedCompletionGate $profile}

$fixture=New-GateFixture;Bind-GateFixture $fixture
$identity=[pscustomobject]@{CompletionWorkflowGateSha256=$fixture.gateSha256;CompletionGateProtocolVersion=1}
$ReviewedCompletionGatePath=$null;$ReviewedCompletionGateSha256=$null
Expect-Rejected {Get-ReviewedCompletionGate $profile} # ordinary update cannot drop it

$fixture=New-GateFixture;Bind-GateFixture $fixture
$gate=Get-ReviewedCompletionGate $profile
$operation=[Guid]::NewGuid();$operationIdentity=$fixture.resumeSha256
$null=Invoke-CompletionGate $gate 'claim-resume' $operation $operationIdentity $null (Join-Path $package 'app') $fixture.resumePath
$expired=@{Bytes=[Text.Encoding]::UTF8.GetBytes((@{ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddSeconds(-1);CatalogExpiresAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(10)}|ConvertTo-Json))}
Expect-Rejected {Assert-CompletionInstallEffect $gate $operation $operationIdentity $expired}

$fixture=New-GateFixture;Bind-GateFixture $fixture
$identity=[pscustomobject]@{Product='CloudScribe Pro'}
Expect-Rejected {Get-ReviewedCompletionGate $profile} # legacy binary cannot activate

$fixture=New-GateFixture;Bind-GateFixture $fixture
[IO.File]::WriteAllText((Join-Path $target 'app/CloudScribe.dll'),'old inert app')
Expect-Rejected {Get-ReviewedCompletionGate $profile} # must already be exact hook build

# A build handoff is a distinct, independently pinned pre-financial operation.
# Only inert fixture DLL bytes and private files below this fresh temporary root change.
$buildScript=Join-Path $PSScriptRoot '../tools/completion_workflow/make_synthetic_build_fixture.py'
$buildRoot=Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
$buildMetadata=& $PythonPath -I -S $buildScript $buildRoot
if($LASTEXITCODE -ne 0){throw 'Synthetic build handoff setup failed.'}
$build=$buildMetadata|ConvertFrom-Json
Bind-GateFixture $build
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$ReviewedCompletionBuildTransitionPath=$build.transitionPath
$ReviewedCompletionBuildTransitionSha256='b'*64
Expect-Rejected {Get-ReviewedCompletionGate $profile}
$ReviewedCompletionBuildTransitionSha256=$build.transitionSha256
$gate=Get-ReviewedCompletionGate $profile
if(-not $gate.BuildTransition -or $gate.Sha256 -cne $build.gateSha256){throw 'Exact reviewed build handoff not read.'};$passed++
$operation=[Guid]$build.transitionId;$operationIdentity=$build.transitionSha256
$null=Invoke-CompletionGate $gate 'claim-build' $operation $operationIdentity $null (Join-Path $package 'app');$passed++
$null=Invoke-CompletionGate $gate 'check-build' $operation $operationIdentity $null (Join-Path $package 'app');$passed++
Expect-Rejected {Get-ReviewedCompletionGate $profile} # pending cannot start a new installation
Expect-Rejected {Invoke-CompletionGate $gate 'claim-build' $operation $operationIdentity $null (Join-Path $package 'app')}
$required=Join-Path $profile '.completion-workflow-required.json'
$beforeRequired=(Get-FileHash -LiteralPath $required -Algorithm SHA256).Hash
Expect-Rejected {Complete-CompletionBuildInstall $gate (Join-Path $target 'app')} # old files are not success evidence
if((Get-FileHash -LiteralPath $required -Algorithm SHA256).Hash -cne $beforeRequired){throw 'Failed readback changed predecessor marker.'};$passed++
foreach($name in @('CloudScribe.dll','CloudScribe.Infrastructure.dll')){Copy-Item -LiteralPath (Join-Path $package ('app/'+$name)) -Destination (Join-Path $target ('app/'+$name))}
[IO.File]::WriteAllBytes((Join-Path $target 'completion-workflow-gate.json'),$gate.Bytes)
@{Product='CloudScribe Pro';Version=1;Profile=$profile;AppSha256=$gate.Config.AppSha256.ToUpperInvariant();CompletionGateProtocolVersion=1;CompletionWorkflowGateSha256=$gate.Sha256}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json')
Complete-CompletionBuildInstall $gate (Join-Path $target 'app');$passed++
$installedGate=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config}
$status=Invoke-CompletionGate $installedGate 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne 1 -or $status.resumeAdmissions -ne 0 -or $status.completionAdmissions -ne 0 -or $status.pendingId -or $status.scopeSha256 -cne $build.scopeSha256 -or [DateTimeOffset]$status.deadlineUtc -ne $gate.DeadlineUtc){throw 'Build succession reset counts, identity or deadline.'};$passed++
Expect-Rejected {Complete-CompletionBuildInstall $gate (Join-Path $target 'app')} # completed handoff cannot replay

# A separately reviewed helper repair preserves the completed build and every
# original counter. It changes source pins only, never the scope or binaries.
$helperRoot=Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
$helperMetadata=& $PythonPath -I -S $buildScript $helperRoot --helper-source
if($LASTEXITCODE -ne 0){throw 'Synthetic helper handoff setup failed.'}
$helper=$helperMetadata|ConvertFrom-Json
Bind-GateFixture $helper
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
$ReviewedCompletionBuildTransitionPath=$helper.transitionPath
$ReviewedCompletionBuildTransitionSha256=$helper.transitionSha256
$gate=Get-ReviewedCompletionGate $profile;$passed++
$operation=[Guid]$helper.transitionId;$operationIdentity=$helper.transitionSha256
$null=Invoke-CompletionGate $gate 'claim-build' $operation $operationIdentity $null (Join-Path $package 'app');$passed++
$null=Invoke-CompletionGate $gate 'check-build' $operation $operationIdentity $null (Join-Path $package 'app');$passed++
Expect-Rejected {Get-ReviewedCompletionGate $profile}
Expect-Rejected {Complete-CompletionBuildInstall $gate (Join-Path $target 'app')} # old helper pin is not completion evidence
[IO.File]::WriteAllBytes((Join-Path $target 'completion-workflow-gate.json'),$gate.Bytes)
@{Product='CloudScribe Pro';Version=1;Profile=$profile;AppSha256=$gate.Config.AppSha256.ToUpperInvariant();CompletionGateProtocolVersion=1;CompletionWorkflowGateSha256=$gate.Sha256}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json')
Complete-CompletionBuildInstall $gate (Join-Path $target 'app');$passed++
$installedGate=[pscustomobject]@{Path=(Join-Path $target 'completion-workflow-gate.json');Sha256=$gate.Sha256;Config=$gate.Config}
$status=Invoke-CompletionGate $installedGate 'status' ([Guid]::Empty) '' $null (Join-Path $target 'app')
if($status.refreshes -ne 1 -or $status.resumeAdmissions -ne 0 -or $status.completionAdmissions -ne 0 -or $status.buildTransitions -ne 1 -or $status.helperSourceRepairs -ne 1 -or $status.pendingId -or $status.scopeSha256 -cne $helper.scopeSha256 -or [DateTimeOffset]$status.deadlineUtc -ne $gate.DeadlineUtc){throw 'Helper succession changed the build count, scope, funds or deadline.'};$passed++
Expect-Rejected {Complete-CompletionBuildInstall $gate (Join-Path $target 'app')}

[pscustomobject]@{Passed=$passed;ProviderCalls=0;CredentialOperations=0;RealInstalls=0;FixtureParent=$fixtureParent}
