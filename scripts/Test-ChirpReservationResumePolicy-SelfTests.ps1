$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'Resume fixtures require PowerShell 7 on .NET 8 or later.'}
$tokens=$null;$errors=$null
$installer=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer failed parsing.'}
$articleTests=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-ChirpArticlePolicy-SelfTests.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Article fixture failed parsing.'}
foreach($name in @('Assert-NoLinks','Assert-UniqueRecoveryJson','Assert-RecoveryRecord','Assert-ChirpPolicyHashes','Assert-ChirpRange','Assert-ChirpLineage','Assert-ChirpLegacyAssociations','Assert-ChirpSuccessorGrant','Assert-ChirpPolicy','Assert-ChirpResumePolicy','Get-ReviewedChirpResumePolicy','New-ArticlePolicy')){
    $from=if($name -eq 'New-ArticlePolicy'){$articleTests}else{$installer}
    $functions=@($from.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($functions.Count -ne 1){throw 'Expected one reviewed fixture/validator.'};Invoke-Expression $functions[0].Extent.Text
}
$sha='a'*64;$id=[Guid]::NewGuid().ToString('D')
function New-ResumePolicy {
    $now=[DateTimeOffset]::UtcNow;$history=New-ArticlePolicy;$history.ExpiresAtUtc=$now.AddHours(-1).ToString('O')
    $envelope=@{AccountId='synthetic-account';CredentialReferenceId='synthetic-reference';CapabilityProvenanceId='synthetic-capability';PricingProvenanceId='synthetic-price';RequestRevision=1;VoiceName='cmn-CN-Chirp3-HD-Alnilam';AudioEncoding='MP3';CompiledPayloadSha256=$sha;CompiledPayloadBytes=100;RecoveryAuthorizationId=$null;ChirpAuthorizationId=$id}
    $execution=$envelope.Clone();$execution.RequestRevision=2
    return @{SchemaVersion=1;MappingId=[Guid]::NewGuid().ToString('D');AppSha256=$sha;InfrastructureSha256=$sha;ProfileRootSha256=$sha;SourceApprovalSha256=('d'*64);ExpiresAtUtc=$now.AddMinutes(15).ToString('O');HistoricalPolicy=$history;AuthorizationId=$id;OriginalAttemptRowSha256=$sha;CleanQueueRowSha256=$sha;FinancialSha256=$sha;OriginalApprovedAtUtc=$now.AddHours(-2).ToString('O');OriginalEnvelope=$envelope;ExecutionEnvelope=$execution;CatalogSnapshotId=$id;CatalogExpiresAtUtc=$now.AddMinutes(30).ToString('O');ProviderEvidenceSha256=$sha;SelectionSha256=$sha;RuntimeEvidenceSha256=$sha;RequestIdentitySha256=$sha;MaximumExistingDispatches=1;MaximumNewReservations=0;MaximumAdditionalReservationMinorUnits=0}
}
function Test-ResumeJson([string]$text){
    $doc=[Text.Json.JsonDocument]::Parse($text)
    try{Assert-UniqueRecoveryJson $doc.RootElement;Assert-ChirpResumePolicy $doc.RootElement}finally{$doc.Dispose()}
}
$passed=0
Test-ResumeJson ((New-ResumePolicy)|ConvertTo-Json -Depth 24 -Compress);$passed++
$successor=New-ResumePolicy;$successor.PriorMappingSha256='b'*64;Test-ResumeJson ($successor|ConvertTo-Json -Depth 24 -Compress);$passed++
foreach($case in @('expiry','catalog-expiry','new-attempt','new-funds','two-dispatches','old-approval','profile','historical-expiry','payload','credential','voice','both-voices','both-payloads','unknown-field','missing-hash','empty-identity','invalid-predecessor')){
    $p=New-ResumePolicy
    switch($case){
        'expiry' {$p.ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')}
        'catalog-expiry' {$p.CatalogExpiresAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(1).ToString('O')}
        'new-attempt' {$p.MaximumNewReservations=1}
        'new-funds' {$p.MaximumAdditionalReservationMinorUnits=1}
        'two-dispatches' {$p.MaximumExistingDispatches=2}
        'old-approval' {$p.SourceApprovalSha256=$p.HistoricalPolicy.SourceApprovalSha256}
        'profile' {$p.ProfileRootSha256='0'*64}
        'historical-expiry' {$p.OriginalApprovedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
        'payload' {$p.ExecutionEnvelope.CompiledPayloadSha256='b'*64}
        'credential' {$p.ExecutionEnvelope.CredentialReferenceId='changed'}
        'voice' {$p.ExecutionEnvelope.VoiceName='changed'}
        'both-voices' {$p.ExecutionEnvelope.VoiceName='changed';$p.OriginalEnvelope.VoiceName='changed'}
        'both-payloads' {$p.ExecutionEnvelope.CompiledPayloadSha256='b'*64;$p.OriginalEnvelope.CompiledPayloadSha256='b'*64}
        'unknown-field' {$p.CallerApproved=$true}
        'missing-hash' {$p.Remove('RequestIdentitySha256')}
        'empty-identity' {$p.MappingId=[Guid]::Empty.ToString('D')}
        'invalid-predecessor' {$p.PriorMappingSha256='not-a-reviewed-hash'}
    }
    $rejected=$false;try{Test-ResumeJson ($p|ConvertTo-Json -Depth 24 -Compress)}catch{$rejected=$true}
    if(-not $rejected){throw "Invalid resume scope accepted: $case"};$passed++
}
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-resume-fixtures-'+[Guid]::NewGuid().ToString('N'))
$package=Join-Path $fixtureRoot 'package';$profile=Join-Path $fixtureRoot 'profile';New-Item -ItemType Directory -Path (Join-Path $package 'app'),$profile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.dll'),'inert synthetic app');[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.Infrastructure.dll'),'inert synthetic infrastructure')
$p=New-ResumePolicy;$p.AppSha256=(Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash.ToLowerInvariant();$p.InfrastructureSha256=(Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash.ToLowerInvariant()
$canonical=[IO.Path]::GetFullPath($profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant();$p.ProfileRootSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant();$p.HistoricalPolicy.ProfileRootSha256=$p.ProfileRootSha256
$ReviewedChirpResumePolicyPath=Join-Path $fixtureRoot 'resume.private.json';[IO.File]::WriteAllText($ReviewedChirpResumePolicyPath,($p|ConvertTo-Json -Depth 24 -Compress));$ReviewedChirpResumePolicySha256=(Get-FileHash -LiteralPath $ReviewedChirpResumePolicyPath).Hash
if(-not (Get-ReviewedChirpResumePolicy $profile)){throw 'Reviewed fixture was not loaded.'};$passed++
$ReviewedChirpResumePolicySha256='0'*64;$rejected=$false;try{Get-ReviewedChirpResumePolicy $profile|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Unapproved pin accepted.'};$passed++
$ReviewedChirpResumePolicySha256=(Get-FileHash -LiteralPath $ReviewedChirpResumePolicyPath).Hash
$rejected=$false;try{Get-ReviewedChirpResumePolicy (Join-Path $fixtureRoot 'other-profile')|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Another profile accepted.'};$passed++
[IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.dll'),'changed inert bytes');$rejected=$false;try{Get-ReviewedChirpResumePolicy $profile|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Changed build accepted.'};$passed++
[pscustomobject]@{Passed=$passed;ProviderCalls=0;Installs=0;CredentialOperations=0;FixtureRoot=$fixtureRoot}
