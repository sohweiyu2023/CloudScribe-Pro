$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -CompletionFunctionsOnly
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-ChirpArticlePolicy-SelfTests.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Synthetic fixture failed parsing.'}
foreach($name in @('New-ArticlePolicy','New-RefinedArticlePolicy','New-WholeArticlePolicy','New-SentenceArticlePolicy','New-SuccessorArticlePolicy','New-SentenceDiagnosticPolicy')){
    $function=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($function.Count -ne 1){throw 'Expected one exact synthetic constructor.'}
    Invoke-Expression $function[0].Extent.Text
}
$sha='a'*64;$id=[Guid]::NewGuid().ToString('D')
$now=[DateTimeOffset]::UtcNow;$history=New-SentenceDiagnosticPolicy
$history.ExpiresAtUtc=$now.AddMinutes(-1).ToString('O')
$envelope=@{AccountId='synthetic-account';CredentialReferenceId='synthetic-reference';CapabilityProvenanceId='synthetic-capability';PricingProvenanceId='synthetic-price';RequestRevision=1;VoiceName='cmn-CN-Chirp3-HD-Alnilam';AudioEncoding='MP3';CompiledPayloadSha256=$sha;CompiledPayloadBytes=100;RecoveryAuthorizationId=$null;ChirpAuthorizationId=$id}
$execution=$envelope.Clone();$execution.RequestRevision=2
$policy=@{SchemaVersion=1;MappingId=[Guid]::NewGuid().ToString('D');AppSha256=$sha;InfrastructureSha256=$sha;ProfileRootSha256=$sha;SourceApprovalSha256=('d'*64);ExpiresAtUtc=$now.AddMinutes(10).ToString('O');HistoricalPolicy=$history;AuthorizationId=$id;OriginalAttemptRowSha256=$sha;CleanQueueRowSha256=$sha;FinancialSha256=$sha;OriginalApprovedAtUtc=$now.AddMinutes(-2).ToString('O');OriginalEnvelope=$envelope;ExecutionEnvelope=$execution;CatalogSnapshotId=$id;CatalogExpiresAtUtc=$now.AddMinutes(30).ToString('O');ProviderEvidenceSha256=$sha;SelectionSha256=$sha;RuntimeEvidenceSha256=$sha;RequestIdentitySha256=$sha;MaximumExistingDispatches=1;MaximumNewReservations=0;MaximumAdditionalReservationMinorUnits=0}
function Test-Policy($p){
    $doc=[Text.Json.JsonDocument]::Parse(($p|ConvertTo-Json -Depth 24 -Compress))
    try{Assert-UniqueRecoveryJson $doc.RootElement;Assert-ChirpResumePolicy $doc.RootElement}finally{$doc.Dispose()}
}
Test-Policy $policy
$passed=1
foreach($case in @('new-funds','second-dispatch','old-approval','expired','historical-time','changed-payload')){
    $p=($policy|ConvertTo-Json -Depth 24 -Compress)|ConvertFrom-Json -AsHashtable
    switch($case){
        'new-funds' {$p.MaximumNewReservations=1}
        'second-dispatch' {$p.MaximumExistingDispatches=2}
        'old-approval' {$p.SourceApprovalSha256=$p.HistoricalPolicy.SourceApprovalSha256}
        'expired' {$p.ExpiresAtUtc=$now.AddSeconds(-1).ToString('O')}
        'historical-time' {$p.OriginalApprovedAtUtc=$now.ToString('O')}
        'changed-payload' {$p.ExecutionEnvelope.CompiledPayloadSha256='b'*64}
    }
    $rejected=$false;try{Test-Policy $p}catch{$rejected=$true}
    if(-not $rejected){throw "Invalid synthetic reserved diagnostic policy accepted: $case"};$passed++
}
Write-Output "PASS: $passed native historical-diagnostic resume policy checks; no provider/install/live profile."
