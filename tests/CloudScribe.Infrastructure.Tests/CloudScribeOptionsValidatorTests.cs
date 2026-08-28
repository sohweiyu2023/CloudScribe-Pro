using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class CloudScribeOptionsValidatorTests
{
    [Fact]
    public void RejectsRemoteTelemetryBeforeConsentImplementationExists()
    {
        CloudScribeOptionsValidator validator = new();
        CloudScribeOptions options = new() { RemoteTelemetryEnabled = true };

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        IEnumerable<string> failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);
        Assert.Contains(failures, failure => failure.Contains("RemoteTelemetryEnabled", StringComparison.Ordinal));
    }
    [Fact]
    public void RejectsUnboundedSupportBundleFileCounts()
    {
        CloudScribeOptionsValidator validator = new();
        CloudScribeOptions options = new() { SupportBundleMaximumFileCount = 0 };

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("SupportBundleMaximumFileCount", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInvalidDiagnosticMaximumFileCount()
    {
        CloudScribeOptions options = new() { DiagnosticMaximumFileCount = 0 };

        ValidateOptionsResult result = new CloudScribeOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("DiagnosticMaximumFileCount", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(121)]
    public void RejectsUnboundedStartupTimeouts(int seconds)
    {
        CloudScribeOptions options = new() { StartupTimeoutSeconds = seconds };

        ValidateOptionsResult result = new CloudScribeOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("StartupTimeoutSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("relative/cloudscribe")]
    [InlineData("//server/share/cloudscribe")]
    [InlineData(@"\\server\share\cloudscribe")]
    public void RejectsUnsafeApplicationDataOverrides(string configuredPath)
    {
        CloudScribeOptions options = new() { AppDataDirectoryOverride = configuredPath };

        ValidateOptionsResult result = new CloudScribeOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("AppDataDirectoryOverride", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsFilesystemRootApplicationDataOverride()
    {
        CloudScribeOptions options = new()
        {
            AppDataDirectoryOverride = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())),
        };

        ValidateOptionsResult result = new CloudScribeOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("filesystem root", StringComparison.Ordinal));
    }

}
