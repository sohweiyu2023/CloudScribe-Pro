[CmdletBinding()]
param([string]$PythonPath)
$ErrorActionPreference='Stop'
if(-not $PythonPath){throw 'Supply the existing reviewed Python executable with -PythonPath.'}
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$temporaryBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot=Join-Path $temporaryBase ('cloudscribe-administrative-adapter-test-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $testRoot
$count=0
function Assert-Test([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
try{
    $operatorPath=Join-Path $testRoot 'inert-reviewed-operator.py'
    @'
import json,sys
plan=json.load(open(sys.argv[1],encoding='utf-8'))
mode=plan['Mode']
if mode=='malformed':
    print('not JSON');sys.exit(1)
if mode in ('denied','redaction'):
    print(json.dumps(dict(rejected=True,code='RecoveryMutableEvidence',exceptionType='ValueError',
        sourceComponent='fake-private-canary' if mode=='redaction' else 'pending_install_operator.py',
        sourceLine=42,systemErrorCode=32)));sys.exit(1)
print(json.dumps(dict(authority=mode=='authority',providerRequests=0,valid=True,restored=True)))
'@ | Set-Content -LiteralPath $operatorPath -Encoding utf8
    $configPath=Join-Path $testRoot 'inert-config.json'
    @{PythonPath=$PythonPath;PythonSha256=(Get-FileHash -LiteralPath $PythonPath -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json|Set-Content -LiteralPath $configPath -Encoding utf8
    $planPath=Join-Path $testRoot 'inert-plan.json'
    $plan=@{AdministrativeOnly=$true;PaidAuthority=$false;OperatorPath=$operatorPath;SourceSha256=@{'pending_install_operator.py'=(Get-FileHash -LiteralPath $operatorPath -Algorithm SHA256).Hash.ToLowerInvariant()};ImmutableNewGate=@{Path=$configPath;Sha256=(Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()};Mode='good'}
    function Write-TestPlan{ $plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $planPath -Encoding utf8;return (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    $pin=Write-TestPlan
    foreach($phase in @('inspect','restore','resume')){
        $result=Invoke-PendingCompletionInstallWithdrawal $planPath $pin $phase
        Assert-Test (-not $result.authority -and $result.providerRequests -eq 0) "Unexpected $phase transport result."
    }
    foreach($mode in @('denied','redaction','authority','malformed')){
        $plan.Mode=$mode;$pin=Write-TestPlan;$failure=$null
        try{Invoke-PendingCompletionInstallWithdrawal $planPath $pin 'restore'|Out-Null}catch{$failure=$_.Exception.Message}
        Assert-Test ([bool]$failure) "Adapter did not reject $mode."
        Assert-Test ($failure -cnotmatch 'fake-private-canary') 'Private canary escaped safe adapter metadata.'
        if($mode -eq 'denied'){Assert-Test ($failure -match 'RecoveryMutableEvidence.*component=pending_install_operator.py.*line=42') 'Safe known failure details were discarded.'}
    }
    $failure=$null
    try{Invoke-PendingCompletionInstallWithdrawal $planPath ('a'*64) 'restore'|Out-Null}catch{$failure=$_.Exception.Message}
    Assert-Test ($failure -match 'plan pin mismatch') 'Wrong plan hash did not fail before helper execution.'
    [pscustomobject]@{Passed=$count;ProviderCalls=0;RealProfileAccess=$false;Scope='Synthetic installer transport only; actual Windows restoration tested separately'}
    # Expected native failures above were asserted. Do not leak their exit into CI's wrapper.
    $global:LASTEXITCODE=0
}finally{
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith($temporaryBase,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('cloudscribe-administrative-adapter-test-',[StringComparison]::Ordinal)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
