param([Parameter(Mandatory)][string]$PythonPath,[Parameter(Mandatory)][string]$EvidencePath)
$ErrorActionPreference='Stop'
$installer=Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed.'}
$functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Wait-CompletionBridgeProcess'},$true))
if($functions.Count -ne 1){throw 'Expected exact production wait function.'}
Invoke-Expression $functions[0].Extent.Text
$base=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot=Join-Path $base ('cloudscribe-owned-timeout-'+[Guid]::NewGuid().ToString('N'))
if(-not $testRoot.StartsWith($base.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic root escaped.'}
$null=New-Item -ItemType Directory -Path $testRoot
$helper=Join-Path $testRoot 'synthetic-child.py'
@'
from pathlib import Path
import os,sys,time
root=Path(sys.argv[1]);mode=sys.argv[2]
if mode=='normal':
    (root/'normal-exit').write_text('synthetic')
elif mode=='control':
    (root/'control-started').write_text(str(os.getpid()))
    while not (root/'control-stop').exists():time.sleep(.05)
else:
    (root/'pre-timeout-commit').write_text('synthetic committed before timeout')
    (root/'delayed-started').write_text(str(os.getpid()))
    time.sleep(20)
    (root/'late-effect').write_text('must never occur')
'@ | Set-Content -LiteralPath $helper -Encoding utf8
$count=0;$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
function Assert-Test([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
function Start-Synthetic([string]$Mode){
    $info=[Diagnostics.ProcessStartInfo]::new($PythonPath)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    foreach($arg in @('-I','-S','-B',$helper,$testRoot,$Mode)){$info.ArgumentList.Add($arg)}
    $child=[Diagnostics.Process]::Start($info);$owned.Add($child);return $child
}
$timeoutEvent=$null;$passed=$false
try{
    $normal=Start-Synthetic 'normal'
    Wait-CompletionBridgeProcess $normal 'inspect-successor' ([Guid]::NewGuid())
    Assert-Test ($normal.HasExited -and $normal.ExitCode -eq 0) 'Normal child failed.'
    $control=Start-Synthetic 'control'
    $delayed=Start-Synthetic 'delayed'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath (Join-Path $testRoot 'delayed-started')) -and $watch.Elapsed.TotalSeconds -lt 5){Start-Sleep -Milliseconds 25}
    Assert-Test (Test-Path -LiteralPath (Join-Path $testRoot 'delayed-started')) 'Synthetic child failed to start.'
    try{
        Wait-CompletionBridgeProcess $delayed 'claim-successor' ([Guid]::NewGuid())
        throw 'Expected timeout.'
    }catch [TimeoutException]{
        $timeoutEvent=$_.Exception.Data['CompletionBridgeEvent']|ConvertFrom-Json
    }
    Assert-Test ($timeoutEvent.Result -ceq 'Timeout' -and $timeoutEvent.TimeoutMilliseconds -eq 15000) 'Timeout metadata invalid.'
    Assert-Test ($timeoutEvent.ExitConfirmed -eq $true -and $delayed.HasExited) 'Owned child exit unconfirmed.'
    Assert-Test ($timeoutEvent.OutcomeRequiresReconciliation -eq $true -and $timeoutEvent.AutomaticRetry -eq $false) 'Timeout incorrectly claims rollback or retry.'
    Assert-Test (-not $control.HasExited) 'Unrelated control child was stopped.'
    Assert-Test (Test-Path -LiteralPath (Join-Path $testRoot 'pre-timeout-commit')) 'Pre-timeout committed evidence lost.'
    # Cross the helper's original 20-second delayed-effect point after its exit.
    Start-Sleep -Seconds 6
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $testRoot 'late-effect'))) 'Timed-out child performed a late effect.'
    Set-Content -LiteralPath (Join-Path $testRoot 'control-stop') -Value 'synthetic normal shutdown'
    Assert-Test ($control.WaitForExit(5000) -and $control.ExitCode -eq 0) 'Control did not stop normally.'
    $passed=$true
}finally{
    Set-Content -LiteralPath (Join-Path $testRoot 'control-stop') -Value 'synthetic cleanup'
    foreach($child in $owned){
        if(-not $child.HasExited){if(-not $child.WaitForExit(1000)){$child.Kill();$null=$child.WaitForExit(5000)}}
        $child.Dispose()
    }
    $result=[ordered]@{Passed=$passed;Assertions=$count;SyntheticOnly=$true;ProviderCalls=0;CredentialReads=0;ProfileWrites=0;AutomaticRetry=$false;InstallerSha256=(Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant();TestRoot=$testRoot;TimeoutEvent=$timeoutEvent}
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $EvidencePath -Encoding utf8
}
[ordered]@{Passed=$passed;Assertions=$count;SyntheticOnly=$true;ProviderCalls=0;CredentialReads=0;ProfileWrites=0}|ConvertTo-Json -Compress
$global:LASTEXITCODE=0
