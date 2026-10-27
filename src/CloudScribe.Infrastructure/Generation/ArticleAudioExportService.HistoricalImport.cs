using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudScribe.Application.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ArticleAudioExportService
{
    private readonly ConcurrentDictionary<Guid, HistoricalImportPlan> _historicalImports = new();
    private static readonly JsonSerializerOptions HistoricalJsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Evidence must already be admitted by a trusted store, independently of ordinary UI callers.</summary>
    public async Task<ArticleHistoricalImportPreview> PrepareHistoricalImportAsync(
        string admittedEvidenceId,
        GenerationProviderRequest originalRequest, string audioPath, CancellationToken cancellationToken = default)
    {
        var admitted = historicalAnchors is null ? null
            : await historicalAnchors.LoadAdmittedAsync(admittedEvidenceId, cancellationToken).ConfigureAwait(false);
        if (admitted is null || !Same(admitted.EvidenceId, admittedEvidenceId) || admitted.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Independently admitted historical evidence is unavailable.");
        ReadOnlyMemory<byte> reviewedAnchor = admitted.AnchorBytes.ToArray();
        string independentlyReviewedSha256 = admitted.IndependentlyAdmittedSha256;
        if (reviewedAnchor.Length is < 1 or > 16384 || !IsHash(independentlyReviewedSha256)
            || !Same(Hash(reviewedAnchor.Span), independentlyReviewedSha256))
            throw new InvalidOperationException("Historical evidence does not match its independently reviewed digest.");
        using var json = JsonDocument.Parse(reviewedAnchor);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject()
            .GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new InvalidOperationException("Historical evidence is ambiguous.");
        var anchor = JsonSerializer.Deserialize<ArticleHistoricalAudioAnchor>(reviewedAnchor.Span, HistoricalJsonOptions)
            ?? throw new InvalidOperationException("Historical evidence is missing.");
        var request = new GenerationProviderRequest(originalRequest.ProviderStableId, originalRequest.OperationStableId,
            originalRequest.AccountId, originalRequest.IdempotencyKey, originalRequest.CompiledPayload.ToArray(), originalRequest.OutputFormat);
        var plan = new HistoricalImportPlan(anchor, admittedEvidenceId, independentlyReviewedSha256, request,
            Path.GetFullPath(audioPath), DateTimeOffset.UtcNow);
        _ = await VerifyHistoricalImportAsync(plan, cancellationToken).ConfigureAwait(false);
        foreach (var item in _historicalImports)
            if (DateTimeOffset.UtcNow - item.Value.PreparedAt > TimeSpan.FromMinutes(5)) _historicalImports.TryRemove(item.Key, out _);
        if (_historicalImports.Count >= 32) throw new InvalidOperationException("Close earlier historical import reviews first.");
        var id = Guid.NewGuid();
        if (!_historicalImports.TryAdd(id, plan)) throw new InvalidOperationException("Historical review could not be prepared.");
        return new(id, plan.AnchorSha256, anchor.DocumentId, anchor.Ordinal, anchor.VoiceName, anchor.LanguageCode, anchor.AudioBytes);
    }

    public async Task ImportReviewedHistoricalAudioAsync(Guid previewId, string displayedReviewSha256,
        bool confirmedByUser, CancellationToken cancellationToken = default)
    {
        if (!confirmedByUser) throw new InvalidOperationException("Historical import requires explicit confirmation of the exact reviewed evidence.");
        if (!_historicalImports.TryRemove(previewId, out var plan) || !Same(displayedReviewSha256, plan.AnchorSha256)
            || DateTimeOffset.UtcNow - plan.PreparedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Historical import review is missing, changed or expired.");
        if (Interlocked.CompareExchange(ref _exportInFlight, 1, 0) != 0)
            throw new InvalidOperationException("Another article operation is active; prepare a new review.");
        try
        {
            var receipt = await VerifyHistoricalImportAsync(plan, cancellationToken).ConfigureAwait(false);
            string destination = Path.Combine(_receipts, receipt.LocalAttemptId + ".json");
            if (File.Exists(destination))
            {
                RejectLinks(destination);
                if (new FileInfo(destination).Length > 16384) throw new InvalidOperationException("Existing provenance is invalid.");
                var existing = JsonSerializer.Deserialize<ArticleAcceptedAudio>(await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false));
                if (existing != receipt) throw new InvalidOperationException("Conflicting historical provenance already exists.");
                return; // Exact repeated reviewed import is idempotent, never another receipt.
            }
            await WriteReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _exportInFlight, 0); }
    }

    private async Task<ArticleAcceptedAudio> VerifyHistoricalImportAsync(HistoricalImportPlan plan, CancellationToken cancellationToken)
    {
        var a = plan.Anchor;
        var admitted = historicalAnchors is null ? null
            : await historicalAnchors.LoadAdmittedAsync(plan.EvidenceId, cancellationToken).ConfigureAwait(false);
        if (admitted is null || !Same(admitted.EvidenceId, plan.EvidenceId) || admitted.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || !Same(admitted.IndependentlyAdmittedSha256, plan.AnchorSha256) || !Same(Hash(admitted.AnchorBytes.Span), plan.AnchorSha256))
            throw new InvalidOperationException("Independent historical evidence changed or expired.");
        if (a.ParentId == Guid.Empty || a.ParentRevisionId == Guid.Empty || a.DocumentId == Guid.Empty || a.RevisionId == Guid.Empty
            || a.Ordinal < 1 || !Guid.TryParseExact(a.LocalAttemptId, "N", out _)
            || !IsHash(a.ParentSha256) || !IsHash(a.TextSha256) || !IsHash(a.PayloadSha256)
            || !IsHash(a.AudioSha256) || !IsHash(a.EvidenceSha256) || a.PayloadBytes is < 1 or > 5000
            || a.AudioBytes is < 12 or > 256L * 1024 * 1024
            || string.IsNullOrWhiteSpace(a.VoiceName) || string.IsNullOrWhiteSpace(a.LanguageCode))
            throw new InvalidOperationException("Historical evidence is incomplete.");
        var request = plan.Request;
        if (!Same(request.AccountId, a.AccountId) || !Same(request.OperationStableId, a.OperationId)
            || !Same(request.IdempotencyKey, a.IdempotencyKey) || !Same(Hash(request.CompiledPayload.Span), a.PayloadSha256)
            || request.CompiledPayload.Length != a.PayloadBytes)
            throw new InvalidOperationException("Historical request identity does not match the reviewed evidence.");
        using var payload = JsonDocument.Parse(request.CompiledPayload);
        var input = payload.RootElement.GetProperty("input");
        if (input.EnumerateObject().Count() != 1 || !input.TryGetProperty("text", out var text)
            || !Same(ArticleAudioManifest.HashText(text.GetString()!), a.TextSha256)
            || !Same(payload.RootElement.GetProperty("voice").GetProperty("name").GetString(), a.VoiceName)
            || !Same(payload.RootElement.GetProperty("voice").GetProperty("languageCode").GetString(), a.LanguageCode)
            || !Same(payload.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString(), "MP3"))
            throw new InvalidOperationException("Historical text, voice or encoding does not match.");
        var (manifest, audio) = await InspectAsync(a.ParentId, cancellationToken).ConfigureAwait(false);
        if (manifest.ParentRevisionId != a.ParentRevisionId || !Same(manifest.ParentSha256, a.ParentSha256)
            || manifest.Members.Select(x => x.DocumentId).Distinct().Count() != manifest.Members.Count
            || !manifest.Members.Select(x => x.Ordinal).SequenceEqual(Enumerable.Range(1, manifest.Members.Count)))
            throw new InvalidOperationException("Historical article grouping changed.");
        var members = manifest.Members.Where(x => x.DocumentId == a.DocumentId && x.Ordinal == a.Ordinal
            && x.RevisionId == a.RevisionId && Same(x.TextSha256, a.TextSha256)).ToArray();
        if (members.Length != 1 || audio.Any(x => x.DocumentId == a.DocumentId && !Same(x.LocalAttemptId, a.LocalAttemptId)))
            throw new InvalidOperationException("Historical part is changed or ambiguous.");
        var receipt = new ArticleAcceptedAudio(a.DocumentId, a.RevisionId, a.TextSha256, a.AccountId, a.OperationId,
            a.IdempotencyKey, a.LocalAttemptId, a.PayloadSha256, plan.AudioPath, a.AudioBytes, a.AudioSha256,
            a.VoiceName, a.LanguageCode, a.EvidenceSha256, plan.AnchorSha256);
        await VerifyQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
        var state = await queue.LoadAsync(a.AccountId, a.OperationId, a.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (state?.PayloadBytes != a.PayloadBytes) throw new InvalidOperationException("Historical payload length is unverified.");
        RejectLinks(plan.AudioPath);
        using var stream = new FileStream(plan.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != a.AudioBytes || !Same(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), a.AudioSha256))
            throw new InvalidOperationException("Historical audio does not match the accepted evidence.");
        return receipt;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c));

    private sealed record HistoricalImportPlan(ArticleHistoricalAudioAnchor Anchor, string EvidenceId, string AnchorSha256,
        GenerationProviderRequest Request, string AudioPath, DateTimeOffset PreparedAt);
}
