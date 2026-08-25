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

        cancellationToken.ThrowIfCancellationRequested();
        paths.EnsureSupportBundleStagingDirectory();

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
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

        string baseName = $"generation-metadata-{bundle.Metadata.CreatedAtUtc:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        string finalPath = Path.Combine(paths.SupportBundleStagingDirectory, baseName);
        string stagingPath = finalPath + ".partial";

        try
        {
            await using (FileStream stream = new(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, finalPath, overwrite: false);
        }
        catch
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

            throw;
        }
    }
}
