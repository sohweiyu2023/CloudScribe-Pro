param([string[]]$Checkpoints)
$ErrorActionPreference='Stop'
# Entirely synthetic inputs. Never execute against a personal installation or profile.
. (Join-Path $PSScriptRoot 'Test-AdditiveArticleAdmission-SelfTests.ps1') -FunctionsOnly
$passed=0
$cases=@(@('PreparedBeforeSeal','False'),@('AppPublished','False'),@('MarkerPublished','False'),@('AdditiveRegistryPublished','False'),@('Complete','False'),@('AppPublished','True'))
foreach($case in $cases){
    if($Checkpoints -and $case[0] -cnotin $Checkpoints){continue}
    $f=New-SyntheticAdmission;Run-Synthetic $f 'Install' $null|Out-Null
    $operations=Join-Path $f.Profile 'data/google-long-audio/operations'
    $guard=Join-Path $operations 'observation-pending.guard'
    if(-not ([IO.Path]::GetFullPath($guard)).StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'SyntheticPathEscaped'}
    Assert-LAPhysical $guard;[IO.File]::Delete($guard)
    $ledger=ConvertFrom-LAJson (Read-LABytes $f.Ledger);$claim=$ledger.Claims[0]
    Save-Synthetic (Join-Path $operations (([Guid]$claim.AttemptId).ToString('N')+'.json')) (ConvertTo-LABytes @{AttemptId=$claim.AttemptId;BindingSha256=$claim.RequestBindingSha256;State=3;OperationParseFailed=$false;Observation=@{ReadbackVerified=$true;ReadState=1;Truncated=$false}})
    & (Join-Path $repo 'tests/CloudScribe.Infrastructure.Tests/LongAudioBuildRebind.Synthetic.ps1') -Repository $repo -Install $f.Install -Profile $f.Profile -Checkpoint $case[0] -Rollback $case[1] -Additive -ConsumeAfterPublication:($case[0] -ceq 'AdditiveRegistryPublished')
    $passed++
}
Write-Output "Additive build rebind synthetic self-tests passed: $passed. No provider calls or live installation."
