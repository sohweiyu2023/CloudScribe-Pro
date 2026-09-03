using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Safety;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class RestoreRecoveryProductionConfigurationResolverTests
{
    [Fact]
    public void ResolveRejectsMissingConfiguredAuthenticationKeyTarget()
    {
        AppPaths paths = CreatePaths();
        RestoreRecoveryProductionConfigurationResolver resolver = new(paths, authenticationKeyTargetName: null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(resolver.Resolve);

        Assert.Contains("not explicitly configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveUsesConfiguredVaultReferenceAndOwnedAbsolutePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-config", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Options.Create(new CloudScribeOptions
        {
            AppDataDirectoryOverride = root,
        }));
        RestoreRecoveryProductionConfigurationResolver resolver = new(paths, "stage8-restore-auth-key");

        RestoreRecoveryProductionConfiguration configuration = resolver.Resolve();

        Assert.Equal("stage8-restore-auth-key", configuration.AuthenticationKeyReference.TargetName);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(root), "restore-recovery", "journal.json"),
            configuration.JournalPath);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(root), "restore-recovery", "staging"),
            configuration.StagingRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "backups"), configuration.BackupRoot);
        Assert.True(Path.IsPathFullyQualified(configuration.JournalPath));
        Assert.True(Path.IsPathFullyQualified(configuration.StagingRoot));
        Assert.True(Path.IsPathFullyQualified(configuration.BackupRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRejectsBlankConfiguredAuthenticationKeyTarget(string configuredTarget)
    {
        AppPaths paths = CreatePaths();
        RestoreRecoveryProductionConfigurationResolver resolver = new(paths, configuredTarget);

        Assert.Throws<InvalidOperationException>(resolver.Resolve);
    }

    private static AppPaths CreatePaths() => new(Options.Create(new CloudScribeOptions
    {
        AppDataDirectoryOverride = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-stage8-config",
            Guid.NewGuid().ToString("N")),
    }));
}
