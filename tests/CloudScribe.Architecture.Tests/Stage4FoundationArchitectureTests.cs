namespace CloudScribe.Architecture.Tests;

public sealed class Stage4FoundationArchitectureTests
{
    [Fact]
    public void Stage4PricingRouteIsTruthfulAboutBlockedExactCatalogAdmission()
    {
        string root = RepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));
        string state = File.ReadAllText(Path.Combine(root, "SESSION_STATE.json"));

        Assert.Contains("Exact catalog contract not admitted", shell, StringComparison.Ordinal);
        Assert.Contains("schema 1.1.5/seed bytes required", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"stage4_catalog_contract_admitted\": false", state, StringComparison.Ordinal);
        Assert.DoesNotContain("requires final native Windows certification before promotion", state, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderControlsAreVisibleButExplainWhyTheyRemainDisabled()
    {
        string root = RepositoryRoot();
        string availability = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "Design", "StageFeatureAvailability.cs"));
        string window = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.axaml"));

        Assert.Contains("Stage4", availability, StringComparison.Ordinal);
        Assert.Contains("ShowProviderControls: true", availability, StringComparison.Ordinal);
        Assert.Contains("No admitted account", window, StringComparison.Ordinal);
        Assert.Contains("stay disabled with explicit reasons", window, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogHistoryIsPersistentAuditedAndNeverSilentlyActivated()
    {
        string root = RepositoryRoot();
        string store = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Pricing", "EfPricingCatalogHistoryStore.cs"));
        string context = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence", "CloudScribeDbContext.cs"));
        string migration = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence", "Migrations", "Stage4PricingCatalogHistory.cs"));
        string window = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.axaml"));

        Assert.Contains("explicit user confirmation", store, StringComparison.Ordinal);
        Assert.Contains("ExpectedCurrentActivationSequence", store, StringComparison.Ordinal);
        Assert.Contains("Rollback can target only", store, StringComparison.Ordinal);
        Assert.Contains("pricing_catalog_snapshots", context, StringComparison.Ordinal);
        Assert.Contains("pricing_catalog_activations", migration, StringComparison.Ordinal);
        Assert.Contains("CATALOG HISTORY", window, StringComparison.Ordinal);
        Assert.Contains("history inspection never activates a catalog", window, StringComparison.Ordinal);
    }


    [Fact]
    public void UserPricingOverridesArePhysicallySeparateAndCannotSilentlyActivate()
    {
        string root = RepositoryRoot();
        string context = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence", "CloudScribeDbContext.cs"));
        string store = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Pricing", "EfPricingContractOverrideStore.cs"));
        string contract = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Application", "Pricing", "IPricingContractOverrideStore.cs"));
        string pricingViewModel = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Pricing.cs"));

        Assert.Contains("pricing_contract_overrides", context, StringComparison.Ordinal);
        Assert.Contains("SaveInactiveAsync", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Activate", contract, StringComparison.Ordinal);
        Assert.Contains("strictJsonReader.Parse", store, StringComparison.Ordinal);
        Assert.Contains("stored inactive override", pricingViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveQuotaObservationsAreProvenanceBearingAndRemainSeparateFromCatalogTruth()
    {
        string root = RepositoryRoot();
        string observation = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Providers.Abstractions", "ProviderQuotaObservation.cs"));
        string quotaSource = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Providers.Abstractions", "IProviderQuotaSource.cs"));
        string pricingViewModel = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Pricing.cs"));

        Assert.Contains("ProvenanceId", observation, StringComparison.Ordinal);
        Assert.Contains("ExpiresAtUtc", observation, StringComparison.Ordinal);
        Assert.Contains("GetQuotaObservationsAsync", quotaSource, StringComparison.Ordinal);
        Assert.Contains("Account quota unknown", pricingViewModel, StringComparison.Ordinal);
    }


    [Fact]
    public void ProviderAccountPersistenceStoresReferencesNotSecretsAndHasNoDefaultSelectionApi()
    {
        string root = RepositoryRoot();
        string entity = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence", "Entities", "ProviderAccountEntity.cs"));
        string contract = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Application", "Providers", "IProviderAccountStore.cs"));
        string window = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.axaml"));

        Assert.Contains("CredentialTargetName", entity, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", entity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", entity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CreateAsync(", contract, StringComparison.Ordinal);
        Assert.Contains("UpdateAsync(", contract, StringComparison.Ordinal);
        Assert.Contains("FindAsync(", contract, StringComparison.Ordinal);
        Assert.Contains("ListAsync(", contract, StringComparison.Ordinal);
        Assert.Equal(4, contract.Split('\n').Count(line => line.TrimStart().StartsWith("Task", StringComparison.Ordinal)));
        Assert.DoesNotContain("DefaultAccount", contract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SelectedAccount", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never selects a default account", window, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityEvidenceIsAppendOnlyLocalInspectionAndNeverImplicitlyRefreshesProvider()
    {
        string root = RepositoryRoot();
        string contract = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Application", "Providers", "IProviderCapabilitySnapshotStore.cs"));
        string store = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Providers", "EfProviderCapabilitySnapshotStore.cs"));
        string pricingViewModel = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Pricing.cs"));

        Assert.Contains("SaveAsync", contract, StringComparison.Ordinal);
        Assert.Contains("GetLatestAsync", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", contract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IProviderAdapter", store, StringComparison.Ordinal);
        Assert.Contains("inspection never refreshes providers", pricingViewModel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogTrustUsesExternalEmptyByDefaultEd25519KeySetWithoutPrivateKeys()
    {
        string root = RepositoryRoot();
        string verifier = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Pricing", "Ed25519PricingCatalogSignatureVerifier.cs"));
        string options = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Pricing", "PricingCatalogTrustOptions.cs"));
        string services = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "DependencyInjection", "ServiceCollectionExtensions.cs"));
        string settings = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "appsettings.json"));

        Assert.Contains("SignatureAlgorithm.Ed25519", verifier, StringComparison.Ordinal);
        Assert.Contains("KeyBlobFormat.RawPublicKey", verifier, StringComparison.Ordinal);
        Assert.Contains("TrustedEd25519PublicKeys", options, StringComparison.Ordinal);
        Assert.Contains("Ed25519PricingCatalogSignatureVerifier", services, StringComparison.Ordinal);
        Assert.DoesNotContain("UnavailablePricingCatalogSignatureVerifier>();", services, StringComparison.Ordinal);
        Assert.Contains("\"TrustedEd25519PublicKeys\": {}", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateKey", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PRIVATE", settings, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root could not be located from test output.");
    }
}
