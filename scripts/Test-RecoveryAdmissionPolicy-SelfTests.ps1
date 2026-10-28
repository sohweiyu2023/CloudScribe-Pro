$ErrorActionPreference='Stop'
# Pure synthetic parser/validator fixtures. No installer execution, profiles, policy admission or provider access.
if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'These optional-policy fixtures require PowerShell 7 on .NET 8 or later.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer source failed parsing.'}
foreach($name in @('Assert-UniqueRecoveryJson','Assert-RecoveryRecord','Write-RecoveryCanonicalRecord','Get-RecoveryCanonicalGrantSha256','Assert-RecoveryNestedPolicy')){
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
function New-SuccessorPolicy {
    $p=New-SyntheticPolicy
    $p.SchemaVersion=3;$p.HistoricalAudio=@();$p.Budget.TransitionRows=@()
    $p.Budget.AllocationCommittedMinorUnits=13;$p.Budget.AllocationCeilingMinorUnits=83
    $id=[Guid]::NewGuid()
    $p.Predecessors[0].Successor=@{PriorAuthorizationId=$id.ToString('D');PriorChildKey=('recovery-google-tts:'+$id.ToString('N'));PriorStateSha256=$sha;PriorAdmissionSha256=('b'*64);FinancialStateSha256=$sha;SourceApprovalSha256=('c'*64);AllocationTransition=@{PriorScopeSha256=$sha;PriorAllocationCeilingMinorUnits=80;NewAllocationCeilingMinorUnits=83;NewRemainingMinorUnits=70;SourceApprovalSha256=('c'*64)}}
    $doc=[Text.Json.JsonDocument]::Parse(($p|ConvertTo-Json -Depth 16 -Compress))
    try{$p.Predecessors[0].AdmissionSha256=Get-RecoveryCanonicalGrantSha256 $doc.RootElement.GetProperty('Predecessors')[0]}finally{$doc.Dispose()}
    return $p
}
Test-PolicyJson ((New-SuccessorPolicy) | ConvertTo-Json -Depth 16 -Compress);$passed++
foreach($failure in @('missing-link','unknown-link','missing-audit-pins','wrong-child','same-old-grant','bad-financial-hash','unbounded','import','transition-source','transition-remaining','transition-ceiling','unknown-transition','wrong-nullable-type','schema2-link','schema2-pins','currency','scale','canonical-digest')){
    $p=New-SuccessorPolicy
    switch($failure){
        'missing-link' {$p.Predecessors[0].Remove('Successor')}
        'unknown-link' {$p.Predecessors[0].Successor.Other='unexpected'}
        'missing-audit-pins' {$p.Budget.Remove('TransitionRows')}
        'wrong-child' {$p.Predecessors[0].Successor.PriorChildKey='wrong-child'}
        'same-old-grant' {$p.Predecessors[0].Successor.PriorAdmissionSha256=$p.Predecessors[0].AdmissionSha256}
        'bad-financial-hash' {$p.Predecessors[0].Successor.FinancialStateSha256='invalid'}
        'unbounded' {$p.Predecessors[0].MaximumNewAttempts=2}
        'import' {$p.HistoricalAudio=(New-SyntheticPolicy).HistoricalAudio}
        'transition-source' {$p.Predecessors[0].Successor.AllocationTransition.SourceApprovalSha256=$sha}
        'transition-remaining' {$p.Predecessors[0].Successor.AllocationTransition.NewRemainingMinorUnits=71}
        'transition-ceiling' {$p.Predecessors[0].Successor.AllocationTransition.NewAllocationCeilingMinorUnits=80}
        'unknown-transition' {$p.Predecessors[0].Successor.AllocationTransition.Extra=1}
        'wrong-nullable-type' {$p.Predecessors[0].Successor.AllocationTransition='invalid'}
        'canonical-digest' {$p.Predecessors[0].AdmissionSha256=$sha}
        'currency' {$p.Currency='EUR'}
        'scale' {$p.Scale=3}
        'schema2-pins' {$p=New-SyntheticPolicy;$p.Budget.TransitionRows=@()}
        'schema2-link' {$p.SchemaVersion=2;$p.Budget.Remove('TransitionRows')}
    }
    $rejected=$false
    try{Test-PolicyJson ($p|ConvertTo-Json -Depth 16 -Compress)}catch{$rejected=$true}
    if(-not $rejected){throw "Synthetic invalid successor policy was not rejected: $failure"};$passed++
}
[pscustomobject]@{Passed=$passed;Failed=0;SyntheticOnly=$true;InstallerExecuted=$false;ProviderCalls=0}
