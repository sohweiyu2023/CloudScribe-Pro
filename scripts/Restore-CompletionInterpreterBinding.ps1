[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GateFile,
    [Parameter(Mandatory)][string]$GateSha256,
    [Parameter(Mandatory)][string]$TransitionFile,
    [Parameter(Mandatory)][string]$TransitionSha256,
    [Parameter(Mandatory)][string]$WithdrawalFile,
    [Parameter(Mandatory)][string]$WithdrawalSha256,
    [switch]$ValidateOnly,
    [switch]$CompletePending
)
$ErrorActionPreference='Stop'
if($ValidateOnly -and $CompletePending){throw 'Pending withdrawal completion is a distinct operation.'}
$withdrawValidateOnly=$ValidateOnly;$withdrawCompletePending=$CompletePending
# Loads shared production validators only; no normal installer effects run.
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
function Read-ReviewedWithdrawalInput([string]$Path,[string]$Pin){
    $full=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path);Assert-NoLinks $full
    if($Pin -cnotmatch '^[a-f0-9]{64}$' -or (Get-Item -LiteralPath $full).Length -gt 32768 -or (Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant() -cne $Pin){throw 'Reviewed withdrawal input pin differs.'}
    [byte[]]$bytes=[IO.File]::ReadAllBytes($full);$doc=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($bytes))
    try{Assert-UniqueRecoveryJson $doc.RootElement}finally{$doc.Dispose()}
    return [pscustomobject]@{Path=$full;Sha256=$Pin;Bytes=$bytes;Record=([Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json)}
}
$material=Read-ReviewedWithdrawalInput $GateFile $GateSha256
$repair=Read-ReviewedWithdrawalInput $TransitionFile $TransitionSha256
$withdrawal=Read-ReviewedWithdrawalInput $WithdrawalFile $WithdrawalSha256
$config=$material.Record
$root=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/CloudScribe Pro'))
if([IO.Path]::GetFullPath($config.ApplicationDirectory) -ine (Join-Path $root 'app') -or $repair.Record.TransitionKind -cnotin @('interpreter-binding','whole-article-interpreter-continuation')){throw 'Withdrawal requires the existing stable installation and exact interpreter repair.'}
$markerBackup=Join-Path $withdrawal.Record.BackupPath 'previous-install.json';Assert-NoLinks $markerBackup
if((Get-Item -LiteralPath $markerBackup).Length -gt 32768 -or (Get-FileHash -LiteralPath $markerBackup).Hash.ToLowerInvariant() -cne $withdrawal.Record.PreviousInstallSha256){throw 'Withdrawal backup differs from its reviewed pin.'}
$marker=Get-Content -LiteralPath $markerBackup -Raw|ConvertFrom-Json
$gate=[pscustomobject]@{Path=$material.Path;Sha256=$material.Sha256;Bytes=$material.Bytes;Config=$config;RequiredPath=(Join-Path $marker.Profile '.completion-workflow-required.json');BuildTransition=[pscustomobject]@{Path=$repair.Path;Sha256=$repair.Sha256;Id=[Guid]$repair.Record.TransitionId;Kind=$repair.Record.TransitionKind;Record=$repair.Record};Withdrawal=$withdrawal}
if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'Close CloudScribe normally before withdrawal; no process will be killed.'}
$operation=[Guid]$withdrawal.Record.WithdrawalId
$command=if($withdrawCompletePending){'check-interpreter-withdrawal'}else{'inspect-interpreter-withdrawal'}
$null=Invoke-CompletionGate $gate $command $operation $withdrawal.Sha256 $null $config.ApplicationDirectory
if($withdrawValidateOnly){[pscustomobject]@{Valid=$true;SafetyOnly=$true;ProviderCalls=0;FinancialWrites=0};return}
if(-not $withdrawCompletePending){$null=Invoke-CompletionGate $gate 'claim-interpreter-withdrawal' $operation $withdrawal.Sha256 $null $config.ApplicationDirectory}
Restore-CompletionInterpreterPins $gate
[pscustomobject]@{Withdrawn=$true;RunPermanentlyStopped=$true;OldInterpreterExecuted=$false;PaidAuthorityRestored=$false;ProviderCalls=0;FinancialWrites=0}
