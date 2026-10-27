using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ReviewedHistoricalImportIsExplicitIdempotentAndDoesNotWriteFinancialState()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Historical synthetic", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue(); var native = new ArticleNative();
        var evidence = await HistoricalEvidenceAsync(fixture, batch, queue);
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, native, evidence.Store);
        var preview = await service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", evidence.Request, evidence.Path, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportReviewedHistoricalAudioAsync(preview.PreviewId, preview.ReviewSha256, false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "accepted-article-audio")));
        await service.ImportReviewedHistoricalAudioAsync(preview.PreviewId, preview.ReviewSha256, true, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportReviewedHistoricalAudioAsync(preview.PreviewId, preview.ReviewSha256, true, TestContext.Current.CancellationToken));
        var repeat = await service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", evidence.Request, evidence.Path, TestContext.Current.CancellationToken);
        await service.ImportReviewedHistoricalAudioAsync(repeat.PreviewId, repeat.ReviewSha256, true, TestContext.Current.CancellationToken);
        var inspected = await service.InspectAsync(batch.Parent.Id, TestContext.Current.CancellationToken);
        var receipt = Assert.Single(inspected.Manifest.RequireComplete(inspected.Audio));
        Assert.Equal(evidence.Digest, receipt.HistoricalAnchorSha256);
        Assert.Equal(evidence.Anchor.EvidenceSha256, receipt.HistoricalEvidenceSha256);
        Assert.Equal(evidence.Anchor.AudioSha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(evidence.Path, TestContext.Current.CancellationToken))));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "accepted-article-audio"), "*.json"));
        Assert.Equal(0, queue.Writes); Assert.Equal(0, native.Calls);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("account")]
    [InlineData("operation")]
    [InlineData("ordinal")]
    [InlineData("revision")]
    [InlineData("voice")]
    [InlineData("language")]
    [InlineData("audio")]
    [InlineData("payload-length")]
    [InlineData("uncertain")]
    public async Task HistoricalEvidenceMismatchNeverPublishesReceipt(string mismatch)
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("PRIVATE_HISTORICAL_SENTINEL", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue();
        var e = await HistoricalEvidenceAsync(fixture, batch, queue);
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, new ArticleNative(), e.Store);
        var changed = mismatch switch
        {
            "account" => e.Anchor with { AccountId = "another-account" },
            "operation" => e.Anchor with { OperationId = "another-operation" },
            "ordinal" => e.Anchor with { Ordinal = 2 },
            "revision" => e.Anchor with { RevisionId = Guid.NewGuid() },
            "voice" => e.Anchor with { VoiceName = "another-voice" },
            "language" => e.Anchor with { LanguageCode = "zh-CN" },
            "audio" => e.Anchor with { AudioSha256 = new string('b', 64) },
            "payload-length" => e.Anchor with { PayloadBytes = e.Anchor.PayloadBytes + 1 },
            _ => e.Anchor,
        };
        if (string.Equals(mismatch, "uncertain", StringComparison.Ordinal)) queue.State = queue.State! with { UnresolvedSubmission = true };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(changed);
        string digest = string.Equals(mismatch, "digest", StringComparison.Ordinal) ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(json));
        e.Store.Value = new("synthetic-reviewed-anchor", json, digest, DateTimeOffset.UtcNow.AddHours(1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", e.Request, e.Path, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("PRIVATE_HISTORICAL_SENTINEL", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "accepted-article-audio"))); Assert.Equal(0, queue.Writes);
    }

    [Fact]
    public async Task HistoricalImportRechecksRevisionAndCancellationAfterPreview()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Recheck", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue();
        var e = await HistoricalEvidenceAsync(fixture, batch, queue);
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, new ArticleNative(), e.Store);
        var preview = await service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", e.Request, e.Path, TestContext.Current.CancellationToken);
        var child = batch.Children[0];
        await fixture.Library.SaveAsync(new DocumentSaveRequest(child.Id, child.Title, child.Text + "changed", child.ConcurrencyVersion, DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportReviewedHistoricalAudioAsync(preview.PreviewId, preview.ReviewSha256, true, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "accepted-article-audio")));
    }

    private static async Task<HistoricalEvidence> HistoricalEvidenceAsync(Fixture fixture, Batch batch, ArticleQueue queue)
    {
        var child = batch.Children[0];
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = child.Text }, voice = new { languageCode = "en-US", name = "synthetic-voice" }, audioConfig = new { audioEncoding = "MP3" } });
        string attempt = Guid.NewGuid().ToString("N"), key = child.Id.ToString("N"), path = Path.Combine(fixture.Root, "retained-history.mp3");
        byte[] audio = new byte[128]; audio[0] = 42;
        await File.WriteAllBytesAsync(path, audio, TestContext.Current.CancellationToken).ConfigureAwait(false);
        queue.State = new("synthetic-account", "synthetic-operation", key, false, null, true, attempt,
            "google-accepted", Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
        queue.States[key] = queue.State;
        var anchor = new ArticleHistoricalAudioAnchor(batch.Parent.Id, batch.Parent.CurrentRevisionId!.Value, Hash(batch.Parent.Text), 1,
            child.Id, child.CurrentRevisionId!.Value, Hash(child.Text), "synthetic-account", "synthetic-operation", key, attempt,
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length, Convert.ToHexStringLower(SHA256.HashData(audio)), audio.Length,
            "synthetic-voice", "en-US", new string('a', 64));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(anchor);
        string digest = Convert.ToHexStringLower(SHA256.HashData(json));
        return new(anchor, json, digest,
            new("synthetic-provider", "synthetic-operation", "synthetic-account", key, payload, "mp3"), path,
            new() { Value = new("synthetic-reviewed-anchor", json, digest, DateTimeOffset.UtcNow.AddHours(1)) });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("cancelled")]
    [InlineData("audio-changed")]
    public async Task HistoricalImportRequiresCurrentAdmittedEvidenceAndSurvivesInterruption(string failure)
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Admitted synthetic", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue(); var e = await HistoricalEvidenceAsync(fixture, batch, queue);
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, new ArticleNative(), e.Store);
        if (failure is "missing" or "expired")
        {
            e.Store.Value = string.Equals(failure, "missing", StringComparison.Ordinal) ? null : e.Store.Value! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", e.Request, e.Path, TestContext.Current.CancellationToken));
        }
        else
        {
            var p = await service.PrepareHistoricalImportAsync("synthetic-reviewed-anchor", e.Request, e.Path, TestContext.Current.CancellationToken);
            if (string.Equals(failure, "revoked", StringComparison.Ordinal)) e.Store.Value = null;
            if (string.Equals(failure, "audio-changed", StringComparison.Ordinal)) await File.WriteAllBytesAsync(e.Path, new byte[128], TestContext.Current.CancellationToken);
            using var cancel = new CancellationTokenSource();
            if (string.Equals(failure, "cancelled", StringComparison.Ordinal)) cancel.Cancel();
            if (string.Equals(failure, "cancelled", StringComparison.Ordinal))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportReviewedHistoricalAudioAsync(p.PreviewId, p.ReviewSha256, true, cancel.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportReviewedHistoricalAudioAsync(p.PreviewId, p.ReviewSha256, true, TestContext.Current.CancellationToken));
        }
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "accepted-article-audio"))); Assert.Equal(0, queue.Writes);
    }

    private sealed record HistoricalEvidence(ArticleHistoricalAudioAnchor Anchor, byte[] Json, string Digest, GenerationProviderRequest Request, string Path, HistoricalAnchorStore Store);

    private sealed class HistoricalAnchorStore : IReviewedHistoricalAudioAnchorStore
    {
        public ReviewedHistoricalAudioAnchor? Value { get; set; }
        public Task<ReviewedHistoricalAudioAnchor?> LoadAdmittedAsync(string evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(evidenceId, Value?.EvidenceId, StringComparison.Ordinal) ? Value : null);
    }
}
