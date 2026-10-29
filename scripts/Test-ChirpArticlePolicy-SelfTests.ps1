$ErrorActionPreference='Stop'
# Parser/entry fixtures use inert bytes and private temporary paths. No install or provider access.
if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'Article-policy fixtures require PowerShell 7 on .NET 8 or later.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer failed parsing.'}
foreach($name in @('Assert-NoLinks','Assert-UniqueRecoveryJson','Assert-RecoveryRecord','Assert-ChirpPolicyHashes','Assert-ChirpRange','Assert-ChirpLineage','Assert-ChirpPolicy','Get-ReviewedChirpPolicy')){
    $functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($functions.Count -ne 1){throw 'Expected exactly one reviewed validator.'};Invoke-Expression $functions[0].Extent.Text
}
$sha='a'*64;$id=[Guid]::NewGuid().ToString('D')
function New-ArticlePolicy {
    $range=@{StartScalar=0;EndScalarExclusive=3;TextSha256=$sha;ReusedReceiptSha256=$null}
    $plan=@{DocumentId=$id;RevisionId=$id;SourceSha256=$sha;SourceScalars=3;LanguageCode='cmn-CN';VoiceName='cmn-CN-Chirp3-HD-Alnilam';PlannerVersion='chirp3-existing-boundaries-v1';PlanSha256=$sha;Parts=@(@{CoverageOrdinal=1;Range=$range;PayloadSha256=$sha;PayloadBytes=100})}
    $lineage=@{History=@(@{DocumentId=$id;RevisionId=$id;StartScalar=0;EndScalarExclusive=3;TextSha256=$sha;Attempts=@(@{IdempotencyKey='synthetic-old-hold';StateSha256=$sha;RetainedReservationMinorUnits=3})});Ranges=@(@{CoverageOrdinal=1;HistoricalRangeIndexes=@(0)})}
    return @{SchemaVersion=1;AppSha256=$sha;InfrastructureSha256=$sha;ProfileRootSha256=$sha;AccountId='synthetic-account';ProjectId='synthetic-project';Currency='USD';Scale=2;ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O');SourceApprovalSha256=$sha;Budget=@{ProjectCommittedMinorUnits=148;ProjectCeilingMinorUnits=300;AllocationCommittedMinorUnits=18;AllocationCeilingMinorUnits=144;ReviewedEvidenceSha256=$sha;QueueRows=@(@{Identity=$sha;Sha256=$sha});SpendRows=@();RecoveryRows=@();TransitionRows=@()};ScopeRows=@();PriorArticleAttemptRows=@();Articles=@(@{Proposal=$plan;Lineage=$lineage;ReusedAudio=@();AllowedSegments=@(@{CoverageOrdinal=1;MaximumMinorUnits=1})});MaximumNewAttempts=1;MaximumAdditionalReservationMinorUnits=1}
}
function Test-ArticleJson([string]$Text){
    $doc=[Text.Json.JsonDocument]::Parse($Text)
    try{Assert-UniqueRecoveryJson $doc.RootElement;Assert-ChirpPolicy $doc.RootElement}finally{$doc.Dispose()}
}
$passed=0
Test-ArticleJson ((New-ArticlePolicy)|ConvertTo-Json -Depth 24 -Compress);$passed++
foreach($failure in @('unknown','duplicate','missing','wrong-type','expired','unbounded-count','unbounded-cost','budget','duplicate-pin','unknown-nested','gap','cutoff','order','regenerate-reuse','duplicate-allowance','missing-ordinal','range-cap','bad-hash','invalid-native','overlap-index','duplicate-history')){
    $p=New-ArticlePolicy
    switch($failure){
        'unknown' {$p.Override=$true}
        'missing' {$p.Remove('SourceApprovalSha256')}
        'wrong-type' {$p.MaximumNewAttempts='1'}
        'expired' {$p.ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')}
        'unbounded-count' {$p.MaximumNewAttempts=0}
        'unbounded-cost' {$p.MaximumAdditionalReservationMinorUnits=0}
        'budget' {$p.MaximumAdditionalReservationMinorUnits=301}
        'duplicate-pin' {$p.Budget.QueueRows+=@{Identity=$sha;Sha256=$sha}}
        'unknown-nested' {$p.Articles[0].Proposal.CallerApproved=$true}
        'gap' {$p.Articles[0].Proposal.Parts[0].Range.StartScalar=1}
        'cutoff' {$p.Articles[0].Proposal.SourceScalars=4}
        'order' {$p.Articles[0].Proposal.Parts[0].CoverageOrdinal=2}
        'regenerate-reuse' {$p.Articles[0].Proposal.Parts[0].Range.ReusedReceiptSha256=$sha}
        'duplicate-allowance' {$p.Articles[0].AllowedSegments+=@{CoverageOrdinal=1;MaximumMinorUnits=1}}
        'missing-ordinal' {$p.Articles[0].AllowedSegments[0].CoverageOrdinal=2}
        'range-cap' {$p.Articles[0].Proposal.Parts[0].Range.EndScalarExclusive=334;$p.Articles[0].Proposal.SourceScalars=334}
        'bad-hash' {$p.SourceApprovalSha256='bad'}
        'invalid-native' {$p.Articles[0].Proposal.DocumentId=[Guid]::Empty.ToString('D')}
        'overlap-index' {$p.Articles[0].Lineage.Ranges[0].HistoricalRangeIndexes=@(1)}
        'duplicate-history' {$p.Articles[0].Lineage.History[0].Attempts+=@{IdempotencyKey='synthetic-old-hold';StateSha256=$sha;RetainedReservationMinorUnits=3}}
    }
    $json=$p|ConvertTo-Json -Depth 24 -Compress
    if($failure -eq 'duplicate'){$json='{"SchemaVersion":1,'+$json.Substring(1)}
    $rejected=$false;try{Test-ArticleJson $json}catch{$rejected=$true}
    if(-not $rejected){throw "Invalid article-policy fixture accepted: $failure"};$passed++
}
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-chirp-entry-'+[Guid]::NewGuid().ToString('N'))
$package=Join-Path $fixtureRoot 'package';$profile=Join-Path $fixtureRoot 'profile'
try{
    [IO.Directory]::CreateDirectory((Join-Path $package 'app'))|Out-Null;[IO.Directory]::CreateDirectory($profile)|Out-Null
    [IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.dll'),'inert synthetic app')
    [IO.File]::WriteAllText((Join-Path $package 'app/CloudScribe.Infrastructure.dll'),'inert synthetic infrastructure')
    $ReviewedChirpArticlePolicyPath=Join-Path $fixtureRoot 'private-policy.json'
    foreach($case in @('valid','wrong-app','wrong-module','wrong-profile','wrong-digest','expired')){
        $p=New-ArticlePolicy
        $p.AppSha256=(Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash.ToLowerInvariant()
        $p.InfrastructureSha256=(Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash.ToLowerInvariant()
        $canonical=[IO.Path]::GetFullPath($profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()
        $p.ProfileRootSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
        switch($case){'wrong-app' {$p.AppSha256=$sha};'wrong-module' {$p.InfrastructureSha256=$sha};'wrong-profile' {$p.ProfileRootSha256=$sha};'expired' {$p.ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')}}
        [IO.File]::WriteAllText($ReviewedChirpArticlePolicyPath,($p|ConvertTo-Json -Depth 24 -Compress))
        $ReviewedChirpArticlePolicySha256=(Get-FileHash -LiteralPath $ReviewedChirpArticlePolicyPath).Hash
        if($case -eq 'wrong-digest'){$ReviewedChirpArticlePolicySha256=$sha}
        $rejected=$false;$result=$null;try{$result=Get-ReviewedChirpPolicy $profile}catch{$rejected=$true}
        if($case -eq 'valid'){if($rejected -or -not $result.Bytes.Length){throw 'Valid article entry rejected.'}}
        elseif(-not $rejected){throw "Invalid article entry accepted: $case"};$passed++
    }
}finally{
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($fixtureRoot).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected fixture cleanup root.'}
    Assert-NoLinks $fixtureRoot
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
Write-Output "Article admission policy fixtures passed: $passed"
