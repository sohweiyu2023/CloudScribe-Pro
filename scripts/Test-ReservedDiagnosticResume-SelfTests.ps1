[CmdletBinding()]
param([Parameter(Mandatory)][string]$MetadataPath,[string]$ResumePolicyPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$f=Get-Content -LiteralPath $MetadataPath -Raw|ConvertFrom-Json
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
foreach($path in @($MetadataPath,$f.package,$f.target,$f.profile,$f.gatePath,$f.handoffPath)+@($ResumePolicyPath|Where-Object {$_})){
    Assert-NoLinks $path
    if(-not [IO.Path]::GetFullPath($path).StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase)){throw 'Disposable synthetic fixture required.'}
}
$package=$f.package;$target=$f.target;$profile=$f.profile
$ReviewedCompletionGatePath=$f.gatePath;$ReviewedCompletionGateSha256=$f.gateSha256
$ReviewedCompletionSuccessorRunPath=$f.handoffPath;$ReviewedCompletionSuccessorRunSha256=$f.handoffSha256
$ReviewedCompletionBuildTransitionPath=$null;$ReviewedRecoveryPolicyPath=$null
$ReviewedChirpResumePolicyPath=$null;$ReviewedChirpArticlePolicyPath=$null
$CompletePendingCompletionSuccessor=$false;$CompletePendingCompletionInterpreterRepair=$false
$identity=Get-Content -LiteralPath (Join-Path $target 'cloudscribe-install.json') -Raw|ConvertFrom-Json
function Get-Process {param($Name,$ErrorAction)if($Name -cne 'CloudScribe'){throw 'Unexpected process inspection.'};return $null}
if($ResumePolicyPath){
    $doc=[Text.Json.JsonDocument]::Parse([IO.File]::ReadAllBytes($ResumePolicyPath))
    try{Assert-UniqueRecoveryJson $doc.RootElement;Assert-ChirpResumePolicy $doc.RootElement}finally{$doc.Dispose()}
    Write-Output 'PASS: actual native validator accepts exact historical diagnostic policy and zero-new-funds resume.'
}else{
    $gate=Get-ReviewedCompletionGate $profile
    if($gate.SuccessorTransition.Record.SchemaVersion -ne 9 -or $gate.SuccessorTransition.Kind -cne 'expired-reserved-diagnostic-resume'){throw 'Expected exact reserved diagnostic successor.'}
    if(Test-Path -LiteralPath $gate.Config.RegisterPath){throw 'Read-only preflight created a successor.'}
    Write-Output 'PASS: actual installer reader and isolated bridge accept H9 preflight without activation.'
}
