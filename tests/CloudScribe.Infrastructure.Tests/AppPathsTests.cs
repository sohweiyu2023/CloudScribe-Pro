using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void OverrideIsExpandedAndNormalizedToAnAbsolutePath()
    {
        string variableName = "CLOUDSCRIBE_TEST_ROOT";
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-tests", Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, root);
        try
        {
            AppPaths paths = new(Options.Create(new CloudScribeOptions
            {
                AppDataDirectoryOverride = $"%{variableName}%",
            }));

            Assert.True(Path.IsPathFullyQualified(paths.RootDirectory));
            Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "logs"), paths.DiagnosticsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Fact]
    public void DefaultDiagnosticsPreferTheExecutableLogsDirectory()
    {
        AppPaths paths = new(Options.Create(new CloudScribeOptions()));

        Assert.Equal(
            Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "logs"),
            paths.PreferredDiagnosticsDirectory);
        Assert.Equal(paths.PreferredDiagnosticsDirectory, paths.DiagnosticsDirectory);
    }

    [Theory]
    [InlineData("relative/cloudscribe")]
    [InlineData("//server/share/cloudscribe")]
    [InlineData("\\\\server\\share\\cloudscribe")]
    public void RejectsNonLocalOrRelativeOverrides(string configuredPath)
    {
        Assert.Throws<OptionsValidationException>(() => new AppPaths(Options.Create(new CloudScribeOptions
        {
            AppDataDirectoryOverride = configuredPath,
        })));
    }

    [Fact]
    public void RejectsLinkedAppDataRootBeforeCreatingOwnedChildrenWhenSupported()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cloudscribe-app-path-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string physicalTarget = Path.Combine(temporary, "physical-target");
        Directory.CreateDirectory(physicalTarget);
        string linkedRoot = Path.Combine(temporary, "linked-root");
        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedRoot, physicalTarget))
            {
                return;
            }

            AppPaths paths = new(Options.Create(new CloudScribeOptions
            {
                AppDataDirectoryOverride = linkedRoot,
            }));

            Assert.Throws<InvalidOperationException>(paths.EnsureDatabaseDirectory);
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, "data")));
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void RejectsFilesystemRootOverride()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        Assert.Throws<OptionsValidationException>(() => new AppPaths(Options.Create(new CloudScribeOptions
        {
            AppDataDirectoryOverride = root,
        })));
    }
}
