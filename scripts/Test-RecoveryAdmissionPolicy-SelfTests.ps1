$ErrorActionPreference='Stop'
# Pure synthetic parser/validator fixtures. No installer execution, profiles, policy admission or provider access.
if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'These optional-policy fixtures require PowerShell 7 on .NET 8 or later.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer source failed parsing.'}
foreach($name in @('Assert-UniqueRecoveryJson','Assert-RecoveryRecord','Assert-RecoveryNestedPolicy')){
    $function=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true)
    if(@($function).Count -ne 1){throw 'Expected one reviewed validator function.'}
    Invoke-Expression $function[0].Extent.Text
}
$sha='a'*64;$guid=[Guid]::NewGuid().ToString('D')
function New-SyntheticPolicy {
    $anchor=@{ParentId=$guid;ParentRevisionId=$guid;ParentSha256=$sha;Ordinal=1;DocumentId=$guid;RevisionId=$guid;TextSha256=$sha;AccountId='synthetic-account';OperationId='synthesize-speech';IdempotencyKey='synthetic-key';LocalAttemptId='synthetic-attempt';PayloadSha256=$sha;PayloadBytes=100;AudioSha256=$sha;AudioBytes=128;VoiceName='synthetic-voice';LanguageCode='en-US';EvidenceSha256=$sha}
    $prior=@{LogicalNarrationId='synthetic-logical';AccountId='synthetic-account';OperationId='synthesize-speech';HistoricalKey='synthetic-key';HistoricalStateSha256=$sha;ProjectId='synthetic-project';DocumentId=$guid;RevisionId=$guid;TextSha256=$sha;OriginalPayloadSha256=$sha;AdmissionSha256=$sha;ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O');MaximumNewAttempts=1;MaximumAdditionalReservationMinorUnits=3}
    return @{SchemaVersion=2;AppSha256=$sha;InfrastructureSha256=$sha;ProfileRootSha256=$sha;AccountId='synthetic-account';ProjectId='synthetic-project';Currency='USD';Scale=2;ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O');Budget=@{ProjectCommittedMinorUnits=250;ProjectCeilingMinorUnits=1000;AllocationCommittedMinorUnits=10;AllocationCeilingMinorUnits=80;ReviewedEvidenceSha256=$sha;QueueRows=@(@{Identity='synthetic-row';Sha256=$sha});SpendRows=@();RecoveryRows=@()};HistoricalAudio=@(@{EvidenceId='synthetic-evidence';Anchor=$anchor});Predecessors=@($prior)}
}
function Test-PolicyJson([string]$Text){
    $document=[Text.Json.JsonDocument]::Parse($Text)
    try{Assert-UniqueRecoveryJson $document.RootElement;Assert-RecoveryNestedPolicy $document.RootElement}finally{$document.Dispose()}
}
$passed=0
Test-PolicyJson ((New-SyntheticPolicy) | ConvertTo-Json -Depth 16 -Compress);$passed++
foreach($failure in @('unknown-nested','missing-field','wrong-type','negative-budget','wrong-operation','wrong-scope','duplicate-pin','invalid-guid','duplicate-json','unreviewed-recovery-pin','duplicate-recovery-pin','old-schema','missing-grant-limit','missing-grant-cap','negative-grant-cap','zero-grant-count','duplicate-grant')){
    $policy=New-SyntheticPolicy
    switch($failure){
        'unknown-nested' {$policy.Budget.UnreviewedOverride=$true}
        'missing-field' {$policy.HistoricalAudio[0].Anchor.Remove('TextSha256')}
        'wrong-type' {$policy.Budget.ProjectCommittedMinorUnits='250'}
        'negative-budget' {$policy.Budget.AllocationCommittedMinorUnits=-1}
        'wrong-operation' {$policy.Predecessors[0].OperationId='another-operation'}
        'wrong-scope' {$policy.HistoricalAudio[0].Anchor.AccountId='another-account'}
        'duplicate-pin' {$policy.Budget.QueueRows+=@{Identity='synthetic-row';Sha256=$sha}}
        'invalid-guid' {$policy.HistoricalAudio[0].Anchor.DocumentId='invalid'}
        'unreviewed-recovery-pin' {$policy.Budget.RecoveryRows=@(@{Identity='synthetic-recovery';Sha256='not-a-hash'})}
        'duplicate-recovery-pin' {$policy.Budget.RecoveryRows=@(@{Identity='synthetic-recovery';Sha256=$sha},@{Identity='synthetic-recovery';Sha256=$sha})}
        'old-schema' {$policy.SchemaVersion=1}
        'missing-grant-limit' {$policy.Predecessors[0].Remove('MaximumNewAttempts')}
        'missing-grant-cap' {$policy.Predecessors[0].Remove('MaximumAdditionalReservationMinorUnits')}
        'negative-grant-cap' {$policy.Predecessors[0].MaximumAdditionalReservationMinorUnits=-1}
        'zero-grant-count' {$policy.Predecessors[0].MaximumNewAttempts=0}
        'duplicate-grant' {$copy=$policy.Predecessors[0].Clone();$copy.HistoricalKey='another-root';$policy.Predecessors+=@($copy)}
    }
    $json=$policy | ConvertTo-Json -Depth 16 -Compress
    if($failure -ceq 'duplicate-json'){$json='{"SchemaVersion":1,'+$json.Substring(1)}
    $rejected=$false
    try{Test-PolicyJson $json}catch{$rejected=$true}
    if(-not $rejected){throw "Synthetic invalid policy was not rejected: $failure"};$passed++
}
[pscustomobject]@{Passed=$passed;Failed=0;SyntheticOnly=$true;InstallerExecuted=$false;ProviderCalls=0}
