param([Parameter(Mandatory)][string]$SourceRoot,[string]$EvidenceRoot=([IO.Path]::GetTempPath()))
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'tools/long-audio/Provisioning.Files.ps1')
$script=Join-Path $SourceRoot 'scripts/Reconcile-LongAudioUnadmitted.ps1'
$checks=0;$roots=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(-not $Value){throw ('FAILED: '+$Name)};$script:checks++}
function NewFixture {
 $root=Join-Path $EvidenceRoot ('cloudscribe-unadmitted-synthetic-'+[Guid]::NewGuid().ToString('N'));$roots.Add($root)
 $install=Join-Path $root 'install';$profile=Join-Path $root 'profile';$ops=Join-Path $profile 'data/google-long-audio/operations';$output=Join-Path $profile 'data/google-long-audio/output'
 foreach($p in @((Join-Path $install 'app'),$ops,$output)){[void][IO.Directory]::CreateDirectory($p)}
 $app=Join-Path $install 'app/CloudScribe.exe';$managed=Join-Path $install 'app/CloudScribe.dll';[IO.File]::WriteAllText($managed,'synthetic-managed-application');$infra=Join-Path $install 'app/CloudScribe.Infrastructure.dll';[IO.File]::WriteAllText($app,'synthetic-not-executable');[IO.File]::WriteAllText($infra,'synthetic-not-executable')
 $scope=Join-Path $install 'google-long-audio-scope.json';Write-LAAtomic $scope (ConvertTo-LABytes @{Version=1;TotalGoogleCapUsdCents=2000})
 $scopeSha=Get-LAHash (Read-LABytes $scope);$id=[Guid]::NewGuid();$binding='a'*64;$stem=$id.ToString('N');$old=[Guid]::NewGuid()
 $ledger=Join-Path $profile 'data/google-long-audio/budget-ledger.json';Write-LAAtomic $ledger (ConvertTo-LABytes @{Version=1;ScopeSha256=$scopeSha;Claims=@(@{AttemptId=$old;RequestBindingSha256=('b'*64);MaximumUsdCents=1})})
 $other=Join-Path $ops ($old.ToString('N')+'.json');Write-LAAtomic $other (ConvertTo-LABytes @{AttemptId=$old;BindingSha256=('b'*64);State=2;OperationParseFailed=$false;Observation=@{ReadbackVerified=$true;Truncated=$false}})
 $journal=Join-Path $ops ($stem+'.json');Write-LAAtomic $journal (ConvertTo-LABytes @{AttemptId=$id;BindingSha256=$binding;State=0;Revision=0;HttpStatus=0;OperationName=$null;RpcCode=$null;Observation=$null;OperationParseFailed=$false;FailureKind=0})
 $guard=Join-Path $ops 'observation-pending.guard';Write-LAAtomic $guard ([Text.Encoding]::ASCII.GetBytes($stem))
 $marker=Join-Path $install 'cloudscribe-install.json';Write-LAAtomic $marker (ConvertTo-LABytes @{Profile=$profile;AppSha256=(Get-LAHash (Read-LABytes $managed));GoogleLongAudioScopeSha256=$scopeSha})
 foreach($lock in @((Join-Path $profile '.instance.lock'),(Join-Path $profile '.whole-article-worker.lock'),(Join-Path $install 'long-audio-provisioning.lock'),(Join-Path $ops 'active-response.lock'),($ledger+'.lock'),(Join-Path $ops 'submission.lock'),(Join-Path $ops ($stem+'.lock')))){[IO.File]::WriteAllBytes($lock,[byte[]]@())}
 $otherInfo=Get-Item -LiteralPath $other;$otherRows=@([ordered]@{Name=$otherInfo.Name;Length=$otherInfo.Length;Sha256=(Get-LAHash (Read-LABytes $other))})
 $plan=[ordered]@{Version=1;InstallRoot=$install;ProfileRoot=$profile;AttemptId=$id;BindingSha256=$binding;AppHostSha256=(Get-LAHash (Read-LABytes $app));AppSha256=(Get-LAHash (Read-LABytes $managed));InfrastructureSha256=(Get-LAHash (Read-LABytes $infra));MarkerSha256=(Get-LAHash (Read-LABytes $marker));ScopeSha256=$scopeSha;LedgerSha256=(Get-LAHash (Read-LABytes $ledger));JournalSha256=(Get-LAHash (Read-LABytes $journal));GuardSha256=(Get-LAHash (Read-LABytes $guard));OtherOperationsSha256=(Get-LAHash (ConvertTo-LABytes $otherRows));OutputInventorySha256=(Get-LAHash ([Text.Encoding]::UTF8.GetBytes('[]')));SourceApprovalSha256=('c'*64);Reason='VerifiedBudgetRejectionBeforeDurableAdmission'}
 $path=Join-Path $root 'plan.json';Write-LAAtomic $path (ConvertTo-LABytes $plan)
 return @{Root=$root;Install=$install;Profile=$profile;Operations=$ops;Output=$output;Ledger=$ledger;Scope=$scope;Other=$other;Journal=$journal;Guard=$guard;Plan=$plan;Path=$path;Sha=(Get-LAHash (Read-LABytes $path));JournalBytes=(Read-LABytes $journal);GuardBytes=(Read-LABytes $guard)}
}
function InvokeFixture($f,[string]$Phase,[scriptblock]$After){& $script -ReviewedPlanPath $f.Path -ReviewedPlanSha256 $f.Sha -Phase $Phase -AfterCheckpoint $After}
function ReSeal($f){Write-LAAtomic $f.Path (ConvertTo-LABytes $f.Plan) -Replace;$f.Sha=Get-LAHash (Read-LABytes $f.Path)}
try{
 foreach($phase in @('Prepared','JournalArchived','Complete')){
  $f=NewFixture;$before=Get-LAHash (Read-LABytes $f.Ledger);[void](InvokeFixture $f 'Inspect' $null)
  $interruptAt=$phase;$checkpoint={param($step)if($step -eq $interruptAt){throw 'Synthetic interruption'}}.GetNewClosure()
  try{[void](InvokeFixture $f 'Archive' $checkpoint);throw 'Expected interruption'}catch{if($_.Exception.Message -notlike '*Synthetic interruption*'){throw}}
  [void](InvokeFixture $f 'Archive' $null);[void](InvokeFixture $f 'Archive' $null)
  $history=Join-Path $f.Install ('long-audio-unadmitted-history/'+$f.Sha)
  Check (-not(Test-Path -LiteralPath $f.Journal) -and -not(Test-Path -LiteralPath $f.Guard)) ($phase+' archived only target')
  Check ((Get-LAHash (Read-LABytes (Join-Path $history 'original-journal.json'))) -ceq (Get-LAHash $f.JournalBytes)) ($phase+' exact journal retained')
  Check ((Get-LAHash (Read-LABytes $f.Ledger)) -ceq $before) ($phase+' financial ledger unchanged')
  [void](InvokeFixture $f 'Rollback' $null);[void](InvokeFixture $f 'Rollback' $null)
  Check ((Get-LAHash (Read-LABytes $f.Journal)) -ceq (Get-LAHash $f.JournalBytes) -and (Get-LAHash (Read-LABytes $f.Guard)) -ceq (Get-LAHash $f.GuardBytes)) ($phase+' exact reversible restoration')
 }
 foreach($fault in @('claimed','observed','operation','revision','guard','other-pending','output','ledger-changed','scope-changed','active-instance','marker-profile','overlap','missing-lock','host-image','managed-image')){
  $f=NewFixture;$held=$null
  switch($fault){
   'claimed' {$book=ConvertFrom-LAJson (Read-LABytes $f.Ledger);$book.Claims+=@{AttemptId=$f.Plan.AttemptId;RequestBindingSha256=$f.Plan.BindingSha256;MaximumUsdCents=1};Write-LAAtomic $f.Ledger (ConvertTo-LABytes $book) -Replace;$f.Plan.LedgerSha256=Get-LAHash (Read-LABytes $f.Ledger);ReSeal $f}
   {$_ -in @('observed','operation','revision')} {$r=ConvertFrom-LAJson (Read-LABytes $f.Journal);if($fault -eq 'observed'){$r.Observation=@{ReadbackVerified=$true}};if($fault -eq 'operation'){$r.OperationName='projects/123456/locations/global/operations/synthetic'};if($fault -eq 'revision'){$r.Revision=1};Write-LAAtomic $f.Journal (ConvertTo-LABytes $r) -Replace;$f.Plan.JournalSha256=Get-LAHash (Read-LABytes $f.Journal);ReSeal $f}
   'guard' {Write-LAAtomic $f.Guard ([Text.Encoding]::ASCII.GetBytes([Guid]::NewGuid().ToString('N'))) -Replace;$f.Plan.GuardSha256=Get-LAHash (Read-LABytes $f.Guard);ReSeal $f}
   'other-pending' {$r=ConvertFrom-LAJson (Read-LABytes $f.Other);$r.State=1;Write-LAAtomic $f.Other (ConvertTo-LABytes $r) -Replace}
   'output' {[IO.File]::WriteAllText((Join-Path $f.Output 'later-output.wav'),'synthetic')}
   'ledger-changed' {[IO.File]::AppendAllText($f.Ledger,' ')}
   'scope-changed' {[IO.File]::AppendAllText($f.Scope,' ')}
   'marker-profile' {$mp=Join-Path $f.Install 'cloudscribe-install.json';$m=ConvertFrom-LAJson (Read-LABytes $mp);$m.Profile=$f.Install;Write-LAAtomic $mp (ConvertTo-LABytes $m) -Replace;$f.Plan.MarkerSha256=Get-LAHash (Read-LABytes $mp);ReSeal $f}
   'overlap' {$f.Plan.ProfileRoot=Join-Path $f.Install 'nested-profile';ReSeal $f}
   'host-image' {[IO.File]::AppendAllText((Join-Path $f.Install 'app/CloudScribe.exe'),'changed')}
   'managed-image' {[IO.File]::AppendAllText((Join-Path $f.Install 'app/CloudScribe.dll'),'changed')}
   'missing-lock' {[IO.File]::Delete((Join-Path $f.Profile '.instance.lock'))}
   'active-instance' {$held=[IO.FileStream]::new((Join-Path $f.Profile '.instance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
  }
  try{$denied=$false;try{[void](InvokeFixture $f 'Archive' $null)}catch{$denied=$true};Check $denied ($fault+' rejects archive');Check (Test-Path -LiteralPath $f.Journal) ($fault+' preserves journal')}finally{if($held){$held.Dispose()}}
 }
 foreach($fault in @('receipt-version','receipt-string-version','receipt-phase','receipt-attestation','receipt-extra','receipt-contradiction')){
  $f=NewFixture;[void](InvokeFixture $f 'Archive' $null);$rp=Join-Path $f.Install ('long-audio-unadmitted-history/'+$f.Sha+'/receipt.json');$r=ConvertFrom-LAJson (Read-LABytes $rp)
  switch($fault){'receipt-version'{$r.Version=2};'receipt-string-version'{$r.Version='1'};'receipt-phase'{$r.Phase='Unknown'};'receipt-attestation'{$r.NoDurableAdmission=$false};'receipt-extra'{$r.Unreviewed='unexpected'};'receipt-contradiction'{$r.Phase='RolledBack'}}
  Write-LAAtomic $rp (ConvertTo-LABytes $r) -Replace
  foreach($phase in @('Inspect','Archive','Rollback')){$denied=$false;try{[void](InvokeFixture $f $phase $null)}catch{$denied=$true};Check $denied ($fault+' rejects '+$phase)}
  Check (-not(Test-Path -LiteralPath $f.Journal)) ($fault+' leaves archived live state unchanged')
 }
 $f=NewFixture;[void](InvokeFixture $f 'Archive' $null)
 try{[void](InvokeFixture $f 'Rollback' {param($step)if($step -ceq 'Restoring'){throw 'Synthetic restore interruption'}});throw 'Expected restore interruption'}catch{if($_.Exception.Message -notlike '*Synthetic restore interruption*'){throw}}
 [void](InvokeFixture $f 'Rollback' $null);Check ((Get-LAHash (Read-LABytes $f.Journal)) -ceq (Get-LAHash $f.JournalBytes)) 'interrupted rollback restores exact record'
 $f=NewFixture;[void](InvokeFixture $f 'Archive' $null);[IO.File]::AppendAllText($f.Ledger,' ')
 $denied=$false;try{[void](InvokeFixture $f 'Rollback' $null)}catch{$denied=$true};Check $denied 'rollback rejects later ledger mutation';Check (-not(Test-Path -LiteralPath $f.Journal)) 'rollback never overwrites later state'
 $f=NewFixture;$folder=Join-Path $f.Output 'verified.article.mp3.work';[void][IO.Directory]::CreateDirectory($folder)
 $media=Join-Path $folder 'retained.wav';[IO.File]::WriteAllText($media,'retained synthetic assembly input')
 $rows=@([ordered]@{Name='verified.article.mp3.work';Directory=$true},[ordered]@{Name=([IO.Path]::Combine('verified.article.mp3.work','retained.wav'));Length=(Get-Item $media).Length;Sha256=(Get-LAHash (Read-LABytes $media))})
 $f.Plan.OutputInventorySha256=Get-LAHash (ConvertTo-LABytes $rows);ReSeal $f
 [void](InvokeFixture $f 'Archive' $null);Check (Test-Path -LiteralPath $media) 'retains completed assembly directory'
 [IO.File]::AppendAllText($media,'changed');$denied=$false;try{[void](InvokeFixture $f 'Rollback' $null)}catch{$denied=$true};Check $denied 'nested output mutation rejects rollback'
 [pscustomobject]@{Passed=$checks;ProviderCalls=0;LiveProfileAccess=0;ScopeOrCapChanges=0;SyntheticRoots=$roots.Count}
}finally{
 # Intentionally retain synthetic fixtures in Temp for independent review and interruption evidence.
}
