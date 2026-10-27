using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task CompleteArticleExportUsesOrderedSnapshotsAndNeverChangesOriginalsOrLedger()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Synthetic article", 2);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue(); var media = new ArticleNative();
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, media);
        var tool = CreateArticleTools(fixture.Root);
        var destination = Path.Combine(fixture.Root, "continuous.mp3");
        var empty = await service.InspectAsync(batch.Parent.Id, TestContext.Current.CancellationToken);
        Assert.Empty(empty.Audio);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(batch.Parent.Id, destination, tool, TestContext.Current.CancellationToken));
        Assert.Equal(0, media.Calls);
        // Record in reverse completion order; membership order must still control export.
        foreach (int index in new[] { 1, 0 }) await RecordArticlePartAsync(service, queue, batch.Children[index], fixture.Root, index);
        await service.ExportAsync(batch.Parent.Id, destination, tool, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2 }, media.InputMarkers);
        Assert.True(File.Exists(destination));
        Assert.Equal(2, Directory.GetFiles(fixture.Root, "accepted-*.mp3").Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(batch.Parent.Id, destination, tool, TestContext.Current.CancellationToken));
        Assert.Equal(0, queue.Writes);
        using var context = fixture.CreateDbContext();
        Assert.Equal(3, await context.Documents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.BillableLedger.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangedOrUncertainAudioFailsBeforeAssemblyWithoutPrivateErrorText()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("PRIVATE_DOCUMENT_SENTINEL", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue(); var media = new ArticleNative();
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, media);
        string tool = CreateArticleTools(fixture.Root);
        await RecordArticlePartAsync(service, queue, batch.Children[0], fixture.Root, 0);
        queue.State = queue.State! with { UnresolvedSubmission = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(batch.Parent.Id,
            Path.Combine(fixture.Root, "new.mp3"), tool, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("PRIVATE_DOCUMENT_SENTINEL", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, media.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "new.mp3")));
    }

    private static string CreateArticleTools(string root)
    {
        string path = Path.Combine(root, "ffmpeg.exe");
        File.WriteAllBytes(path, [0]); File.WriteAllBytes(Path.Combine(root, "ffprobe.exe"), [0]);
        return path;
    }

    [Fact]
    public async Task ReceiptPublicationCollisionLeavesValidEvidenceAndNoPendingFile()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Atomic receipt", 1);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var queue = new ArticleQueue();
        var service = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, new ArticleNative());
        await RecordArticlePartAsync(service, queue, batch.Children[0], fixture.Root, 0);
        string receiptPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "accepted-article-audio"), "*.json"));
        byte[] before = await File.ReadAllBytesAsync(receiptPath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => RecordArticlePartAsync(service, queue, batch.Children[0], fixture.Root, 0, reuseAttempt: true));
        Assert.Equal(before, await File.ReadAllBytesAsync(receiptPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "accepted-article-audio"), "*.pending"));
        var inspected = await service.InspectAsync(batch.Parent.Id, TestContext.Current.CancellationToken);
        Assert.Single(inspected.Manifest.RequireComplete(inspected.Audio));
    }

    private static async Task RecordArticlePartAsync(ArticleAudioExportService service, ArticleQueue queue,
        CloudScribe.Application.Documents.DocumentSnapshot document, string root, int index, bool reuseAttempt = false)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = document.Text }, voice = new { name = "synthetic-voice", languageCode = "en-US" } });
        string key = document.Id.ToString("N");
        if (!reuseAttempt) queue.State = new("synthetic-account", "synthetic-operation", key, false, null, true,
            Guid.NewGuid().ToString("N"), "google-accepted", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)));
        queue.States[key] = queue.State ?? throw new InvalidOperationException("Synthetic accepted state missing");
        byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0); audio[10] = (byte)(index + 1);
        string path = Path.Combine(root, $"accepted-{index}.mp3");
        await File.WriteAllBytesAsync(path, audio, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var request = new GenerationProviderRequest("synthetic-provider", "synthetic-operation", "synthetic-account", key, payload, "mp3");
        var outcome = new GoogleGenerationQueueOutcome(new(true, true, "synthetic-accepted"),
            new(SubmissionDisposition.Accepted, null, audio, "audio/mpeg", null, "google-accepted"));
        await service.RecordAcceptedAsync(document, request, outcome, path, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private sealed class ArticleQueue : IGoogleGenerationPersistedQueueStateStore
    {
        public Dictionary<string, GoogleGenerationPersistedQueueState> States { get; } = new(StringComparer.Ordinal);
        public GoogleGenerationPersistedQueueState? State { get; set; }
        public int Writes { get; private set; }
        public Task<GoogleGenerationPersistedQueueState?> LoadAsync(string accountId, string operationStableId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(State?.UnresolvedSubmission == true ? State : States.GetValueOrDefault(idempotencyKey));
        public Task SaveAsync(GoogleGenerationPersistedQueueState state, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException("Export must not write the queue."); }
    }

    private sealed class ArticleNative : INativeMediaTool
    {
        public int Calls { get; private set; }
        public List<byte> InputMarkers { get; } = [];
        public async Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Calls++;
            string output = "";
            if (invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal))
                output = JsonSerializer.Serialize(new { streams = new[] { new { codec_name = "mp3", sample_rate = "24000", channels = 1 } }, format = new { duration = InputMarkers.Count > 0 ? "2" : "1" } });
            else if (invocation.Arguments.Contains("-filter_complex", StringComparer.Ordinal))
            {
                for (int i = 0; i < invocation.Arguments.Count; i++)
                    if (string.Equals(invocation.Arguments[i], "-i", StringComparison.Ordinal)) InputMarkers.Add((await File.ReadAllBytesAsync(invocation.Arguments[i + 1], cancellationToken).ConfigureAwait(false))[10]);
                byte[] bytes = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
                await File.WriteAllBytesAsync(invocation.Arguments[^1], bytes, cancellationToken).ConfigureAwait(false);
            }
            return new(0, false, output, "", TimeSpan.Zero);
        }
    }
}
