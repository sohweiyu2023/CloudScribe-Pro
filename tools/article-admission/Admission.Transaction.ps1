function Set-AAState($Context,[string]$Phase,[scriptblock]$AfterCheckpoint) {
    $state=[ordered]@{SchemaVersion=1;TransactionId=$Context.Plan.TransactionId;PlanSha256=$Context.PlanSha;Phase=$Phase}
    $path=Join-Path $Context.Transaction 'state.json'
    Write-LAAtomic $path (ConvertTo-LABytes $state) -Replace:(Test-Path -LiteralPath $path)
    if($AfterCheckpoint){& $AfterCheckpoint $Phase}
}
function Set-AAImmutable([string]$Root,[string]$Directory,[byte[]]$Bytes,[string]$Extension='json') {
    $folder=Join-Path $Root $Directory;Assert-LAPhysical $folder;[IO.Directory]::CreateDirectory($folder)|Out-Null
    Set-LAExactFile (Join-Path $folder ((Get-LAHash $Bytes)+'.'+$Extension)) $Bytes
}
function Invoke-AAAdmission([string]$Install,[string]$Profile,[string]$PlanPath,[string]$PlanSha,
    [ValidateSet('Inspect','Install','Resume','Rollback')][string]$Phase='Inspect',[scriptblock]$AfterCheckpoint) {
    $c=Get-AAAdmission $Install $Profile $PlanPath $PlanSha
    if($Phase -ceq 'Inspect'){return [pscustomobject]@{Valid=$true;Additive=$true;ProviderCalls=0;RegistryPublished=$c.Published}}
    Assert-LAClosed $c.Install
    $instancePath=Join-Path $c.Profile '.instance.lock';Assert-LAPhysical $instancePath
    $instance=[IO.FileStream]::new($instancePath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $installPath=Join-Path $c.Install 'long-audio-provisioning.lock';Assert-LAPhysical $installPath
        $installLease=[IO.FileStream]::new($installPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try{
            $ledgerPath=Join-Path $c.Profile 'data/google-long-audio/budget-ledger.json.lock';Assert-LAPhysical $ledgerPath
            $budgetLease=[IO.FileStream]::new($ledgerPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            try{
                # Every external pin, closed-profile byte, retained unknown and old/new state is reread under the existing locks.
                Assert-LAClosed $c.Install
                $c=Get-AAAdmission $Install $Profile $PlanPath $PlanSha
                if($c.Published -and $Phase -ceq 'Rollback'){throw 'ArticleAdmissionPublishedHistoryMustBePreserved'}
                if($null -ne $c.State -and $c.State.Phase -ceq 'RolledBack'){
                    if($Phase -cnotin @('Inspect','Rollback')){throw 'ArticleAdmissionRollbackCannotReactivate'}
                    return [pscustomobject]@{RolledBack=$true;AlreadyComplete=$true;HistoryPreserved=$true;ProviderCalls=0}
                }
                if($c.Published){
                    if($null -eq $c.State){throw 'ArticleAdmissionUnownedPublishedState'}
                    if($c.State.Phase -cne 'Complete'){Set-AAState $c 'Complete' $AfterCheckpoint}
                    return [pscustomobject]@{Admitted=$true;AlreadyComplete=$true;ProviderCalls=0;RegistrySha256=$c.NewRegistrySha}
                }
                if($null -eq $c.State){
                    if($Phase -cne 'Install'){throw 'ArticleAdmissionUseInitialInstall'}
                    [IO.Directory]::CreateDirectory($c.Transaction)|Out-Null
                    Set-AAState $c 'Owned' $AfterCheckpoint
                }elseif($Phase -ceq 'Install'){throw 'ArticleAdmissionUseExactResume'}
                if($Phase -ceq 'Rollback'){
                    # No deletion and no profile restore. A published registry is never rolled back, even if apparently unused.
                    Set-AAState $c 'RolledBack' $AfterCheckpoint
                    return [pscustomobject]@{RolledBack=$true;HistoryPreserved=$true;ProviderCalls=0}
                }
                Set-AAImmutable $c.Root 'plans' $c.PlanBytes
                if($null -ne $c.OldRegistryBytes){Set-AAImmutable $c.Root 'registries' $c.OldRegistryBytes}
                Set-AAState $c 'PlanSealed' $AfterCheckpoint
                Set-AAImmutable $c.Root 'grants' $c.GrantBytes
                Set-AAImmutable $c.Root 'native-proofs' $c.NativeBytes
                Set-AAImmutable $c.Root 'source-approvals' $c.SourceBytes 'bin'
                Set-AAState $c 'InputsSealed' $AfterCheckpoint
                Set-AAImmutable $c.Root 'receipts' $c.ReceiptBytes
                Set-AAImmutable $c.Root 'registries' $c.NewRegistryBytes
                Set-AAState $c 'ReceiptSealed' $AfterCheckpoint
                # Publication is the only authority effect, and comes after the complete receipt is durable.
                $c=Get-AAAdmission $Install $Profile $PlanPath $PlanSha
                Write-LAAtomic (Join-Path $c.Root 'registry.json') $c.NewRegistryBytes -Replace:(Test-Path -LiteralPath (Join-Path $c.Root 'registry.json'))
                Set-AAState $c 'RegistryPublished' $AfterCheckpoint
                $c=Get-AAAdmission $Install $Profile $PlanPath $PlanSha
                if(-not $c.Published){throw 'ArticleAdmissionPublicationReadback'}
                Set-AAState $c 'Complete' $AfterCheckpoint
                return [pscustomobject]@{Admitted=$true;ProviderCalls=0;RegistrySha256=$c.NewRegistrySha;History=$c.Transaction}
            }finally{$budgetLease.Dispose()}
        }finally{$installLease.Dispose()}
    }finally{$instance.Dispose()}
}
