using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Batch;

/// <summary>Checks saved narration and original identities before review or an unstarted request. Never filters a provider payload.</summary>
public static class BatchNarrationIdentity
{
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));

    public static Task ValidateAsync(IDocumentLibrary library, BatchSettings settings, BatchItem item, CancellationToken token = default) =>
        ValidateAsync(library, settings, item, null, token);

    public static async Task ValidateAsync(IDocumentLibrary library, BatchSettings settings, BatchItem item, INarrationCopyStore? copies, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(item);
        if (item.JobId is not null || !item.IsIncluded || item.State == BatchItemState.Completed) return;
        var document = item.DocumentId is { } id ? await library.OpenAsync(id, token).ConfigureAwait(false) : null;
        if (document is null || document.CurrentRevisionId != item.RevisionId || document.Status != DocumentStatus.Active)
            throw new InvalidOperationException("The queued narration document changed or is unavailable. Prepare and review it again.");
        if (item.NarrationTextSha256 is { } hash && !string.Equals(hash, Hash(document.Text), StringComparison.Ordinal))
            throw new InvalidOperationException("The queued narration text no longer matches its saved identity.");
        if (!settings.SkipUrls)
        {
            if (item.NarrationSource is not null) throw new InvalidOperationException("The URL skipping option changed after preparation.");
            return;
        }
        if (!NarrationUrlOmission.IsSupportedVersion(settings.UrlTransformVersion) || item.NarrationTextSha256 is null)
            throw new InvalidOperationException("Prepare the narration text with its saved URL skipping option before review.");
        if (copies is not null)
        {
            var stored = await copies.FindAsync(document.Id, token).ConfigureAwait(false);
            var expected = stored is null ? null : new BatchNarrationSource(stored.SourceDocumentId, stored.SourceRevisionId,
                stored.SourceTextSha256, stored.TransformVersion, stored.OmittedUrlCount);
            if (item.NarrationSource != expected)
                throw new InvalidOperationException("The narration copy provenance differs from its durable record.");
        }
        if (item.NarrationSource is not { } source)
        {
            if (NarrationUrlOmission.Transform(document.Text, settings.UrlTransformVersion).OmittedUrlCount != 0)
                throw new InvalidOperationException("URL skipping has not been prepared for this document.");
            return;
        }
        var original = await library.OpenAsync(source.DocumentId, token).ConfigureAwait(false);
        if (original is null || original.Status != DocumentStatus.Active || original.CurrentRevisionId != source.RevisionId
            || !string.Equals(Hash(original.Text), source.TextSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The original changed after the narration copy was prepared. Prepare and review it again.");
        var transformed = NarrationUrlOmission.Transform(original.Text, settings.UrlTransformVersion);
        if (source.DocumentId == document.Id || !string.Equals(source.TransformVersion, transformed.TransformVersion, StringComparison.Ordinal)
            || source.OmittedUrlCount != transformed.OmittedUrlCount || !transformed.HasSpeakableContent
            || !string.Equals(transformed.OutputText, document.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("The narration copy no longer matches the reviewed original and URL skipping option.");
    }
}
