using System.Text;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class DocumentContentStoreTests
{
    [Fact]
    public async Task CommitIsDurableHashedRelativeAndIdempotent()
    {
        string root = CreateTemporaryRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            DocumentContentStore store = new(paths);
            Guid documentId = Guid.NewGuid();
            Guid contentId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("CloudScribe 文档 👋🏽 — durable content\n");

            DocumentContentCommit first = await store.CommitAsync(
                documentId,
                contentId,
                content,
                TestContext.Current.CancellationToken);
            DocumentContentCommit second = await store.CommitAsync(
                documentId,
                contentId,
                content,
                TestContext.Current.CancellationToken);
            byte[] roundTrip = await store.ReadVerifiedAsync(first, TestContext.Current.CancellationToken);

            Assert.Equal(first, second);
            Assert.Equal(content.LongLength, first.ByteLength);
            Assert.Equal(64, first.Sha256.Length);
            Assert.False(Path.IsPathFullyQualified(first.RelativePath));
            Assert.DoesNotContain("..", first.RelativePath, StringComparison.Ordinal);
            Assert.Equal(content, roundTrip);
            Assert.Empty(Directory.EnumerateFiles(paths.DocumentsDirectory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ImmutableContentIdRejectsDifferentBytes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            DocumentContentStore store = new(CreatePaths(root));
            Guid documentId = Guid.NewGuid();
            Guid contentId = Guid.NewGuid();
            await store.CommitAsync(
                documentId,
                contentId,
                Encoding.UTF8.GetBytes("first"),
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(
                documentId,
                contentId,
                Encoding.UTF8.GetBytes("second"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }


    [Fact]
    public async Task ReadRejectsAHashValidPathOutsideDocumentsDirectory()
    {
        string root = CreateTemporaryRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            paths.EnsureRootDirectory();
            string outsidePath = Path.Combine(paths.RootDirectory, "outside.txt");
            byte[] content = Encoding.UTF8.GetBytes("not document content");
            await File.WriteAllBytesAsync(outsidePath, content, TestContext.Current.CancellationToken);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            DocumentContentStore store = new(paths);
            DocumentContentCommit forged = new("outside.txt", hash, content.LongLength);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadVerifiedAsync(forged, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static AppPaths CreatePaths(string root) => new(Options.Create(new CloudScribeOptions
    {
        AppDataDirectoryOverride = root,
    }));

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-document-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
