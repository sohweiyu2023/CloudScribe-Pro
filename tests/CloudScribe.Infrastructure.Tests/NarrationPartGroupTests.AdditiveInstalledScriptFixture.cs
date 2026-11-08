namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    // Runs only against compiler-generated synthetic profiles and inert DLLs under Fixture roots.
    private const string InstalledCatalogBridgeScript = """
        param($Repository,$Install,$Profile,$Inputs,$Phase)
        $ErrorActionPreference='Stop'
        . (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
        foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1','BuildRebind.Transaction.ps1')){
            . (Join-Path $Repository ('tools/long-audio/'+$name))
        }
        foreach($name in @('Admission.Validation.ps1','Admission.Transaction.ps1','BuildRebind.ps1')){
            . (Join-Path $Repository ('tools/article-admission/'+$name))
        }
        $parentPath=Join-Path $Install 'chirp-article-admission-policy.json'
        $scopePath=Join-Path $Install 'google-long-audio-scope.json'
        $ledgerPath=Join-Path $Profile 'data/google-long-audio/budget-ledger.json'
        $markerPath=Join-Path $Install 'cloudscribe-install.json'
        $parent=ConvertFrom-LAJson (Read-LABytes $parentPath)
        $identities=ConvertFrom-LAJson (Read-LABytes (Join-Path $Inputs 'identities.json'))
        if($Phase -ceq 'Admit'){
            $grantPath=Join-Path $Inputs 'grant.json';$family=ConvertFrom-LAJson (Read-LABytes $grantPath)
            $sourcePath=Join-Path $Inputs 'source.txt'
            $plan=[ordered]@{SchemaVersion=1;TransactionId=[Guid]::NewGuid().ToString('D');InstallRoot=$Install;ProfileRoot=$Profile;
                ProfileRootSha256=$parent.ProfileRootSha256;AppSha256=$parent.AppSha256;InfrastructureSha256=$parent.InfrastructureSha256;
                SourceApprovalPath=$sourcePath;SourceApprovalSha256=(Get-LAHash (Read-LABytes $sourcePath));
                InstallMarkerSha256=(Get-LAHash (Read-LABytes $markerPath));LegacyPolicySha256=$identities.LegacyPolicySha256;
                LegacyPolicyFileSha256=(Get-LAHash (Read-LABytes $parentPath));LegacyScopeSha256=(Get-LAHash (Read-LABytes $scopePath));
                LegacyLedgerSha256=(Get-LAHash (Read-LABytes $ledgerPath));ProfileTreeSha256=(Get-LABuildTreeHash $Profile);
                RetainedEvidenceTreeSha256=(Get-LABuildTreeHash (Join-Path $Profile 'data/google-long-audio'));
                PreviousRegistrySha256=$null;PreviousSidecarTreeSha256=$null;GrantPath=$grantPath;GrantSha256=(Get-LAHash (Read-LABytes $grantPath));
                NativeProofPath=(Join-Path $Inputs 'synthetic-native-proof.json');NativeProofSha256=('0'*64)}
            $native=[ordered]@{SchemaVersion=1;Passed=$true}
            foreach($key in @('AppSha256','InfrastructureSha256','ProfileRootSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256',
                'LegacyScopeSha256','LegacyLedgerSha256','ProfileTreeSha256','RetainedEvidenceTreeSha256','GrantSha256','SourceApprovalSha256')){$native[$key]=$plan[$key]}
            $native.FamilyId=$family.FamilyId;$native.ReviewedBaselineSha256=$family.ReviewedBaselineSha256
            $native.FamilyPolicySha256=$identities.FamilyPolicySha256;$native.AllInCeilingMinorUnits=$family.AllInCeilingMinorUnits
            $native.ProvisionMinorUnits=$family.ProvisionMinorUnits;$native.ProviderCalls=0;$native.LiveWrites=0
            [IO.File]::WriteAllBytes($plan.NativeProofPath,(ConvertTo-LABytes $native));$plan.NativeProofSha256=Get-LAHash (Read-LABytes $plan.NativeProofPath)
            $planPath=Join-Path $Inputs 'admit-plan.json';[IO.File]::WriteAllBytes($planPath,(ConvertTo-LABytes $plan))
            $result=Invoke-AAAdmission $Install $Profile $planPath (Get-LAHash (Read-LABytes $planPath)) 'Install'
            if(-not $result.Admitted -or $result.ProviderCalls -ne 0){throw 'SyntheticBridgeAdmissionDidNotComplete'}
        }elseif($Phase -ceq 'Rebind'){
            $package=Join-Path $Inputs 'package';$newParent=Join-Path $Inputs 'new-parent.json';$newScope=Join-Path $Inputs 'new-scope.json'
            $sourcePath=Join-Path $Inputs 'build-source.txt';$root=Join-Path $Install 'chirp-article-admissions'
            $ledger=ConvertFrom-LAJson (Read-LABytes $ledgerPath)
            $plan=[ordered]@{Version=3;TransactionId=[Guid]::NewGuid().ToString('D');SourceApprovalSha256=(Get-LAHash (Read-LABytes $sourcePath));
                PreviousMarkerSha256=(Get-LAHash (Read-LABytes $markerPath));PreviousParentSha256=(Get-LAHash (Read-LABytes $parentPath));
                PreviousScopeSha256=(Get-LAHash (Read-LABytes $scopePath));PreviousLedgerSha256=(Get-LAHash (Read-LABytes $ledgerPath));
                PreviousAppTreeSha256=(Get-LABuildTreeHash (Join-Path $Install 'app'));
                PreviousProvisioningTreeSha256=(Get-LABuildTreeHash (Join-Path $Install 'long-audio-provisioning'));
                ProfileTreeSha256=(Get-LABuildTreeHash $Profile);PackageAppTreeSha256=(Get-LABuildTreeHash (Join-Path $package 'app'));
                NewParentPath=$newParent;NewParentSha256=(Get-LAHash (Read-LABytes $newParent));NewScopePath=$newScope;NewScopeSha256=(Get-LAHash (Read-LABytes $newScope));
                NativeProofPath=(Join-Path $Inputs 'synthetic-build-native-proof.json');NativeProofSha256=('0'*64);
                DeferredEvidenceSha256=(Get-LAHash ([Text.Encoding]::UTF8.GetBytes('null')));
                AdditiveRegistrySha256=(Get-LAHash (Read-LABytes (Join-Path $root 'registry.json')));AdditiveSidecarTreeSha256=(Get-LABuildTreeHash $root);
                PreviousLegacyPolicyIdentitySha256=$identities.LegacyPolicySha256;
                NewLegacyPolicyIdentitySha256=([IO.File]::ReadAllText((Join-Path $Inputs 'new-legacy-identity.txt')));SourceApprovalPath=$sourcePath}
            $native=[ordered]@{Version=1;Passed=$true;LongClaimCount=$ledger.Claims.Count;
                LongHeldCents=(@($ledger.Claims|Measure-Object MaximumUsdCents -Sum)[0].Sum);
                ChirpCommittedCents=$parent.Budget.ProjectCommittedMinorUnits;ChirpCeilingCents=$parent.Budget.ProjectCeilingMinorUnits;ProviderCalls=0;LiveWrites=0}
            foreach($key in @('PreviousParentSha256','NewParentSha256','PackageAppTreeSha256','ProfileTreeSha256','DeferredEvidenceSha256',
                'AdditiveRegistrySha256','AdditiveSidecarTreeSha256','PreviousLegacyPolicyIdentitySha256','NewLegacyPolicyIdentitySha256')){$native[$key]=$plan[$key]}
            [IO.File]::WriteAllBytes($plan.NativeProofPath,(ConvertTo-LABytes $native));$plan.NativeProofSha256=Get-LAHash (Read-LABytes $plan.NativeProofPath)
            $planPath=Join-Path $Inputs 'build-plan.json';[IO.File]::WriteAllBytes($planPath,(ConvertTo-LABytes $plan))
            $null=Invoke-LABuildRebind $Install $Profile $package $planPath (Get-LAHash (Read-LABytes $planPath)) 'Install'
        }else{throw 'UnknownSyntheticBridgePhase'}
        Write-Output 'SyntheticInstalledCatalogBridgeVerified'
        """;
}
