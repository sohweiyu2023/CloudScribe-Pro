using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

public sealed class PhysicalDirectoryPolicyTests
{
    private const string RejectionMessage = "The output path cannot traverse a symbolic link or reparse point.";

    [Fact]
    public void CreatesNestedPhysicalDirectoryAndReturnsNormalizedPath()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string requested = Path.Combine(root, "one", "two", "three");

            string result = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(requested, RejectionMessage);

            Assert.Equal(Path.GetFullPath(requested), result);
            Assert.True(Directory.Exists(result));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void RejectsLinkedAncestorBeforeCreatingAnyChildThroughItWhenSupported()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string physicalTarget = Path.Combine(root, "physical-target");
            Directory.CreateDirectory(physicalTarget);
            string link = Path.Combine(root, "linked-parent");
            if (!TryCreateDirectorySymbolicLink(link, physicalTarget))
            {
                return;
            }

            string requested = Path.Combine(link, "must-not-be-created");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(requested, RejectionMessage));

            Assert.Equal(RejectionMessage, exception.Message);
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, "must-not-be-created")));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void RejectsLinkedTargetDirectoryWhenSupported()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string physicalTarget = Path.Combine(root, "physical-target");
            Directory.CreateDirectory(physicalTarget);
            string link = Path.Combine(root, "linked-target");
            if (!TryCreateDirectorySymbolicLink(link, physicalTarget))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(
                () => PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(link, RejectionMessage));
            Assert.Empty(Directory.EnumerateFileSystemEntries(physicalTarget));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-physical-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
