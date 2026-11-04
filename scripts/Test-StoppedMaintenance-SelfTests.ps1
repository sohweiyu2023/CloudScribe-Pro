[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$temporaryBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot=Join-Path $temporaryBase ('cloudscribe-maintenance-adapter-test-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $testRoot
$count=0
function Assert-Test([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
try{
    $operatorPath=Join-Path $testRoot 'maintenance_operator.py'
    @'
import json,sys
with open(sys.argv[1],encoding='utf-8') as stream: mode=json.load(stream)['Mode']
if mode=='duplicate':
    print('{"authority":false,"authority":false,"providerRequests":0,"restored":true}');sys.exit(0)
if mode=='private':
    print(json.dumps(dict(rejected=True,code='PrivateAlphabeticCanary',sourceComponent='PrivateAlphabeticCanary',sourceLine=7)));sys.exit(1)
if mode=='known':
    print(json.dumps(dict(rejected=True,code='MaintenancePending',sourceComponent='maintenance_operator.py',sourceLine=42)));sys.exit(1)
if mode=='malformed': print('not JSON');sys.exit(1)
requests=False if mode=='boolean' else '0' if mode=='string' else 0
print(json.dumps(dict(authority=mode=='authority',providerRequests=requests,restored=mode!='missing',valid=True)))
'@ | Set-Content -LiteralPath $operatorPath -Encoding utf8
    $configPath=Join-Path $testRoot 'inert-config.json'
    @{PythonPath=$PythonPath;PythonSha256=(Get-FileHash -LiteralPath $PythonPath).Hash.ToLowerInvariant();ScriptPath=(Join-Path $testRoot 'gate_cli.py')}|ConvertTo-Json|Set-Content -LiteralPath $configPath -Encoding utf8
    $config=@{Path=$configPath;Sha256=(Get-FileHash -LiteralPath $configPath).Hash.ToLowerInvariant()}
    $planPath=Join-Path $testRoot 'inert-plan.json'
    $plan=@{AdministrativeOnly=$true;PaidAuthority=$false;PreviousGate=$config;NewGate=$config;Mode='good';SourceSha256=@{'maintenance_operator.py'=(Get-FileHash -LiteralPath $operatorPath).Hash.ToLowerInvariant()}}
    function Write-TestPlan{$plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $planPath -Encoding utf8;return (Get-FileHash -LiteralPath $planPath).Hash.ToLowerInvariant()}
    $pin=Write-TestPlan
    foreach($phase in @('inspect','update','resume','rollback','resume-rollback','inspect-release','release','resume-release')){
        $result=Invoke-StoppedMaintenance $planPath $pin $phase
        Assert-Test (-not $result.authority -and $result.providerRequests -eq 0) "Unexpected $phase result."
    }
    foreach($mode in @('boolean','string','duplicate','private','known','authority','missing','malformed')){
        $plan.Mode=$mode;$pin=Write-TestPlan;$failure=$null
        try{Invoke-StoppedMaintenance $planPath $pin 'update'|Out-Null}catch{$failure=$_.Exception.Message}
        Assert-Test ([bool]$failure) "Adapter did not reject $mode."
        Assert-Test ($failure -cnotmatch 'PrivateAlphabeticCanary') 'Private canary escaped adapter.'
        if($mode -eq 'known'){Assert-Test ($failure -match 'MaintenancePending.*component=maintenance_operator.py.*line=42') 'Known actionable reason was discarded.'}
    }
    $failure=$null
    try{Invoke-StoppedMaintenance $planPath ('a'*64) 'update'|Out-Null}catch{$failure=$_.Exception.Message}
    Assert-Test ($failure -match 'plan pin mismatch') 'Wrong plan hash was accepted.'
    [pscustomobject]@{Passed=$count;ProviderCalls=0;RealProfileAccess=$false;Scope='Synthetic transport boundary only'}
    $global:LASTEXITCODE=0
}finally{
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith($temporaryBase,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('cloudscribe-maintenance-adapter-test-',[StringComparison]::Ordinal)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
