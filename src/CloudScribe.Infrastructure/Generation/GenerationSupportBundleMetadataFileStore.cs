using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.Infrastructure.Generation;

internal sealed class GenerationSupportBundleMetadataFileStore(AppPaths paths)
{
    private static readonly string[] ExplicitExclusions =
    [
        "cache-media",
        "compiled-payload",
        "source-text",
        "private-cache-lookup-key",
    ];

    public async Task PersistAsync(
        GenerationSupportBundle bundle,
        CancellationToken cancellationToken = default)
    {
        RequireMetadataOnly(bundle);
        cancellationToken.ThrowIfCancellationRequested();
        paths.EnsureSupportBundleStagingDirectory();

        byte[] payload = BuildPayload(bundle);
        string baseName = $"generation-metadata-{bundle.Metadata.CreatedAtUtc:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        string finalPath = Path.Combine(paths.SupportBundleStagingDirectory, baseName);
        string stagingPath = finalPath + ".partial";

        try
        {
            await WriteStagingFileAsync(stagingPath, payload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, finalPath, overwrite: false);
        }
        catch
        {
            DeleteStagingFile(stagingPath);
            throw;
        }
    }

    private static void RequireMetadataOnly(GenerationSupportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        GenerationSupportBundlePrivacyPolicy.RequireSafe(bundle.PrivacyDecision);
        if (!string.Equals(
                bundle.PrivacyDecision.Reason,
                "support-bundle-metadata-only",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Generation diagnostic persistence accepts only the v2.23 metadata-only privacy decision.");
        }
    }

    private static byte[] BuildPayload(GenerationSupportBundle bundle) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            metadata = bundle.Metadata,
            privacy = new
            {
                bundle.PrivacyDecision.Reason,
                bundle.PrivacyDecision.IncludeCacheMedia,
                bundle.PrivacyDecision.IncludeCompiledPayload,
                bundle.PrivacyDecision.IncludeSourceText,
                bundle.PrivacyDecision.IncludePrivateCacheLookupKey,
            },
            exclusions = ExplicitExclusions,
        });

    private static async Task WriteStagingFileAsync(
        string stagingPath,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        FileStream stream = new(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void DeleteStagingFile(string stagingPath)
    {
        try
        {
            File.Delete(stagingPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
