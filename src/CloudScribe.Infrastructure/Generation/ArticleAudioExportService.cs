using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ArticleAudioExportService(
    IDbContextFactory<CloudScribeDbContext> factory, IDocumentLibrary library,
    IGoogleGenerationPersistedQueueStateStore queue, AppPaths paths, INativeMediaTool native,
    IReviewedHistoricalAudioAnchorStore? historicalAnchors = null)
{
    private readonly string _receipts = Path.Combine(paths.RootDirectory, "accepted-article-audio");
    private int _exportInFlight;

    public async Task RecordAcceptedAsync(DocumentSnapshot document, GenerationProviderRequest request,
        GoogleGenerationQueueOutcome outcome, string audioPath, CancellationToken cancellationToken = default)
    {
        if (document.CurrentRevisionId is not { } revision || document.Status != DocumentStatus.Active
            || outcome.RequiresReconciliation || outcome.Response is not { IsAccepted: true } response
            || !Same(response.DiagnosticCode, "google-accepted") || response.MediaBytes.IsEmpty
            || !string.Equals(response.MediaContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted article output evidence is unavailable.");
        using var payload = JsonDocument.Parse(request.CompiledPayload);
        var input = payload.RootElement.GetProperty("input");
        if (!input.TryGetProperty("text", out var text) || input.TryGetProperty("ssml", out _)
            || !Same(text.GetString(), document.Text))
            throw new InvalidOperationException("Article output requires exact plain-text revision evidence.");
        string payloadHash = Hash(request.CompiledPayload.Span);
        var state = await queue.LoadAsync(request.AccountId, request.OperationStableId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (state is null || state.UnresolvedSubmission || !state.SubmissionAttempted || !Same(state.DiagnosticCode, "google-accepted")
            || !Same(state.PayloadSha256, payloadHash) || !Guid.TryParseExact(state.LocalAttemptId, "N", out _))
            throw new InvalidOperationException("Durable accepted outcome evidence is unavailable.");
        var current = await library.OpenAsync(document.Id, cancellationToken).ConfigureAwait(false);
        if (current?.CurrentRevisionId != revision || !Same(current.Text, document.Text))
            throw new InvalidOperationException("The document changed during generation; output was preserved without an article receipt.");
        string fullPath = Path.GetFullPath(audioPath);
        RejectLinks(fullPath);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(response.MediaBytes.Span))
            throw new InvalidOperationException("Saved output differs from the accepted audio.");
        var receipt = new ArticleAcceptedAudio(document.Id, revision, ArticleAudioManifest.HashText(document.Text),
            request.AccountId, request.OperationStableId, request.IdempotencyKey, state.LocalAttemptId!, payloadHash,
            fullPath, bytes.LongLength, Hash(bytes), payload.RootElement.GetProperty("voice").GetProperty("name").GetString()!,
            payload.RootElement.GetProperty("voice").GetProperty("languageCode").GetString()!);
        await WriteReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteReceiptAsync(ArticleAcceptedAudio receipt, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(receipt.LocalAttemptId, "N", out _) || string.IsNullOrWhiteSpace(receipt.VoiceName)
            || string.IsNullOrWhiteSpace(receipt.LanguageCode)) throw new InvalidOperationException("Output provenance is incomplete.");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (json.Length > 16384) throw new InvalidOperationException("Output provenance exceeds its size bound.");
        RejectLinks(_receipts);
        Directory.CreateDirectory(_receipts);
        string destination = Path.Combine(_receipts, receipt.LocalAttemptId + ".json");
        string temporary = Path.Combine(_receipts, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<(ArticleAudioManifest Manifest, IReadOnlyList<ArticleAcceptedAudio> Audio)> InspectAsync(
        Guid parentId, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var groups = await context.Set<NarrationPartGroupEntity>().AsNoTracking()
            .Where(x => x.ParentDocumentId == parentId && x.Complete).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (groups.Length != 1) throw new InvalidOperationException("Select one original article with a complete, unambiguous part group.");
        var group = groups[0];
        var links = await context.Set<NarrationPartLinkEntity>().AsNoTracking().Where(x => x.GroupId == group.Id)
            .OrderBy(x => x.Ordinal).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var parent = await library.OpenAsync(parentId, cancellationToken).ConfigureAwait(false);
        if (parent is null || parent.Status != DocumentStatus.Active || parent.CurrentRevisionId != group.SourceRevisionId
            || !Same(ArticleAudioManifest.HashText(parent.Text), group.SourceSha256) || links.Length != group.ExpectedPartCount)
            throw new InvalidOperationException("Article text or grouping changed; no export is allowed.");
        List<ArticleAudioMember> members = [];
        var content = new System.Text.StringBuilder();
        foreach (var link in links)
        {
            var child = await library.OpenAsync(link.ChildDocumentId, cancellationToken).ConfigureAwait(false);
            if (child is null || child.Status != DocumentStatus.Active || child.CurrentRevisionId != link.ChildRevisionId
                || !Same(ArticleAudioManifest.HashText(child.Text), link.ChildSha256))
                throw new InvalidOperationException("An article part changed; no export is allowed.");
            content.Append(child.Text);
            members.Add(new(link.ChildDocumentId, link.ChildRevisionId, link.ChildSha256, link.Ordinal));
        }
        if (!Same(content.ToString(), parent.Text)) throw new InvalidOperationException("Article parts do not reconstruct the exact original text.");
        var manifest = new ArticleAudioManifest(parentId, group.SourceRevisionId, group.SourceSha256, members);
        List<ArticleAcceptedAudio> audio = [];
        RejectLinks(_receipts);
        if (Directory.Exists(_receipts))
        {
            var files = Directory.GetFiles(_receipts, "*.json");
            if (files.Length > 10000) throw new InvalidOperationException("Local output evidence exceeds its inspection bound.");
            foreach (string file in files)
            {
                RejectLinks(file);
                if (new FileInfo(file).Length > 16384) throw new InvalidOperationException("Local output evidence is invalid.");
                var receipt = JsonSerializer.Deserialize<ArticleAcceptedAudio>(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("Local output evidence is invalid.");
                if (members.Any(x => x.DocumentId == receipt.DocumentId))
                {
                    await VerifyQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
                    audio.Add(receipt);
                }
            }
        }
        return (manifest, audio);
    }

    private async Task VerifyQueueAsync(ArticleAcceptedAudio receipt, CancellationToken cancellationToken)
    {
        var state = await queue.LoadAsync(receipt.AccountId, receipt.OperationId, receipt.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (state is null || !state.SubmissionAttempted || state.UnresolvedSubmission || !Same(state.DiagnosticCode, "google-accepted")
            || !Same(state.LocalAttemptId, receipt.LocalAttemptId) || !Same(state.PayloadSha256, receipt.PayloadSha256))
            throw new InvalidOperationException("Accepted output requires reconciliation or lacks exact outcome evidence.");
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static void RejectLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Linked paths are not supported for article output.");
            current = Path.GetDirectoryName(current);
        }
    }
}
