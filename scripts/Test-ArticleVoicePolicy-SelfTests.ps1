$ErrorActionPreference='Stop'
# Parser-only inert fixtures. Compiler integrity/native proof and paid submission are separate gates.
. (Join-Path $PSScriptRoot 'Test-AdditiveArticleAdmission-SelfTests.ps1') -FunctionsOnly
$f=New-SyntheticAdmission;$family=ConvertFrom-LAJson (Read-LABytes $f.Plan.GrantPath);$policy=$family.Policy
$neural=$policy.Articles[0].Proposal
$neural.SourceScalars=400;$neural.Parts[0].Range.EndScalarExclusive=400;$neural.Parts[0].PayloadBytes=500
$policy.Articles[0].Lineage.History[0].EndScalarExclusive=400
function Test-SyntheticPolicy($Policy){
    $json=[Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString((ConvertTo-LABytes $Policy)))
    try{Assert-ChirpPolicy $json.RootElement}finally{$json.Dispose()}
}
Test-SyntheticPolicy $policy;$passed=1
foreach($fault in @('wrong-family','wrong-version','wrong-language','oversized-payload','scalar-byte-bound','input-format','refinement','successor','legacy-chirp-bound','diagnostic-grant')){
    $p=ConvertFrom-LAJson (ConvertTo-LABytes $policy);$n=$p.Articles[0].Proposal
    switch($fault){
        'wrong-family' {$n.VoiceName='en-US-Standard-D'}
        'wrong-version' {$n.PlannerVersion='caller-chosen-neural2-version'}
        'wrong-language' {$n.LanguageCode='en-GB'}
        'oversized-payload' {$n.Parts[0].PayloadBytes=5001}
        'scalar-byte-bound' {$n.Parts[0].PayloadBytes=399}
        'input-format' {$n.InputFormat=@{Version=1}}
        'refinement' {$n.Refinement=@{}}
        'successor' {$n.SentenceSuccessor=@{}}
        'legacy-chirp-bound' {$n.VoiceName='en-US-Chirp3-HD-Aoede';$n.PlannerVersion='chirp3-existing-boundaries-v1'}
        'diagnostic-grant' {$p.SuccessorGrant=@{}}
    }
    Expect-SyntheticRejection {Test-SyntheticPolicy $p};$passed++
}
Write-Output "Article voice parser self-tests passed: $passed. No admission or provider calls."
