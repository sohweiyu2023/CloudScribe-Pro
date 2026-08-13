using System.Security.Cryptography;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Files;

public sealed class DocumentContentStore(AppPaths appPaths)
{
    private const string LinkRejectionMessage =
        "CloudScribe document storage cannot traverse a symbolic link or reparse point.";

    public async Task<DocumentContentCommit> CommitAsync(
        Guid documentId,
        Guid contentId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        }

        if (contentId == Guid.Empty)
        {
            throw new ArgumentException("Content ID is required.", nameof(contentId));
        }

        appPaths.EnsureDocumentsDirectory();
        string documentDirectory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
            Path.Combine(appPaths.DocumentsDirectory, documentId.ToString("N")),
            LinkRejectionMessage);
        string contentDirectory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
            Path.Combine(documentDirectory, "content"),
            LinkRejectionMessage);

        string finalPath = Path.Combine(contentDirectory, $"{contentId:N}.utf8");
        string stagingPath = Path.Combine(contentDirectory, $".{contentId:N}.{Guid.NewGuid():N}.tmp");
        string sha256 = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();

        if (File.Exists(finalPath))
        {
            return await VerifyExistingAsync(finalPath, sha256, content.Length, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using (FileStream stream = new(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(stagingPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return await VerifyExistingAsync(finalPath, sha256, content.Length, cancellationToken).ConfigureAwait(false);
            }

            return BuildCommit(finalPath, sha256, content.Length);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    public async Task<byte[]> ReadVerifiedAsync(
        DocumentContentCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        string fullPath = ResolveRelativePath(commit.RelativePath);
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (content.LongLength != commit.ByteLength || !string.Equals(actualHash, commit.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored document content failed its size or SHA-256 integrity check.");
        }

        return content;
    }

    private async Task<DocumentContentCommit> VerifyExistingAsync(
        string finalPath,
        string expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        byte[] existing = await File.ReadAllBytesAsync(finalPath, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
        if (existing.LongLength != expectedLength || !string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new IOException("An immutable document-content ID already exists with different bytes.");
        }

        return BuildCommit(finalPath, actualHash, existing.LongLength);
    }

    private DocumentContentCommit BuildCommit(string fullPath, string sha256, long byteLength)
    {
        string relative = Path.GetRelativePath(appPaths.RootDirectory, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("Document content resolved outside the CloudScribe application-data root.");
        }

        return new DocumentContentCommit(relative.Replace(Path.DirectorySeparatorChar, '/'), sha256, byteLength);
    }

    private string ResolveRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Stored document path must be relative to the CloudScribe application-data root.");
        }

        string normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(appPaths.RootDirectory, normalizedRelative));
        string relativeToDocuments = Path.GetRelativePath(appPaths.DocumentsDirectory, fullPath);
        if (EscapesRoot(relativeToDocuments) || Path.IsPathFullyQualified(relativeToDocuments))
        {
            throw new InvalidDataException("Stored document path escapes the CloudScribe documents directory.");
        }

        _ = PhysicalDirectoryPolicy.ValidateExistsWithoutLinks(Path.GetDirectoryName(fullPath)!, LinkRejectionMessage);
        return fullPath;
    }

    private static bool EscapesRoot(string relativePath)
    {
        if (string.Equals(relativePath, "..", StringComparison.Ordinal))
        {
            return true;
        }

        return relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
