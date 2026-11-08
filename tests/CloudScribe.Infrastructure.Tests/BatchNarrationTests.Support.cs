#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Collections.Concurrent;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Batch;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    private static readonly BatchSettings Settings = new("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "Alnilam (Chirp 3 HD)", "complete-article", BatchFailurePolicy.SkipAndContinue);

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "cloudscribe-batch-" + Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "output");
            Directory.CreateDirectory(Output);
            Store = new FileBatchQueueStore(Path.Combine(Root, "profile"));
        }

        public string Root { get; }
        public string Output { get; }
        public FileBatchQueueStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public async Task<NarrationBatch> CreateAsync(IEnumerable<string> names, BatchSettings? settings = null, Func<string, string?>? text = null)
        {
            var files = names.Select(n => new BatchSourceFile(n, Sha(n), 100, text is null ? "Synthetic text for " + n : text(n))).ToArray();
            var items = BatchPlanner.Plan(files, t => t + ".mp3", name => File.Exists(Path.Combine(Output, name)))
                .Select(x => x.IsIncluded ? x with { DocumentId = Guid.NewGuid(), RevisionId = Guid.NewGuid() } : x).ToArray();
            var chosen = settings ?? Settings;
            var batch = new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Synthetic batch", 0, BatchQueueState.Ready,
                chosen, chosen.Sha256(), Output, "USD", 2, items, DateTimeOffset.UnixEpoch);
            return await Store.SaveAsync(batch, TestContext.Current.CancellationToken);
        }
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>Fake narrator: records every call; behaviour per file order; never contacts a provider.</summary>
    private sealed class FakeNarrator : IBatchNarrator
    {
        public ConcurrentQueue<(int Order, Guid? JobIdAtStart)> Calls { get; } = new();
        public Dictionary<int, Func<BatchItem, CancellationToken, Task<BatchNarrationOutcome>>> Behaviour { get; } = [];
        public HashSet<string> MissingOutputs { get; } = new(StringComparer.Ordinal);
        public bool Authorized { get; set; } = true;
        public TaskCompletionSource? Gate { get; set; }
        public int RecordJobCalls;

        public Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default) =>
            Task.FromResult((Authorized, Authorized ? "ok" : "No batch authority is set up."));

        public async Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob,
            IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Enqueue((item.Order, item.JobId));
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (item.JobId is null) { Interlocked.Increment(ref RecordJobCalls); await recordJob(Guid.NewGuid()); }
            if (Behaviour.TryGetValue(item.Order, out var custom)) return await custom(item, cancellationToken);
            string path = Path.Combine(queue.OutputFolder, item.OutputFileName);
            await File.WriteAllTextAsync(path, "synthetic mp3 " + item.Order, cancellationToken);
            return BatchNarrationOutcome.Completed(path, new FileInfo(path).Length, Sha(path), 120, 130);
        }

        public Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item.OutputPath is not null && File.Exists(item.OutputPath) && !MissingOutputs.Contains(item.OutputFileName));
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class MemoryLibrary : IDocumentLibrary
    {
        public List<DocumentSnapshot> Created { get; } = [];
        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken cancellationToken = default)
        {
            var doc = new DocumentSnapshot(Guid.NewGuid(), title, text, 0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 1);
            Created.Add(doc);
            return Task.FromResult(doc);
        }
        public Task<DocumentSnapshot> SaveAsync(DocumentSaveRequest request, CancellationToken cancellationToken = default)
        {
            var doc = Created.Single(x => x.Id == request.DocumentId) with { Text = request.Text, CurrentRevisionId = Guid.NewGuid(), ConcurrencyVersion = 2 };
            return Task.FromResult(doc);
        }
        public Task<DocumentSnapshot?> OpenAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.FromResult(Created.FirstOrDefault(x => x.Id == documentId));
        public Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentStatus status = DocumentStatus.Active, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(string query, DocumentStatus status = DocumentStatus.Active, int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentSummary> ChangeStatusAsync(Guid documentId, DocumentStatus status, long expectedConcurrencyVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>The ATR-style 14-track layout used as a realistic, non-special example.</summary>
    private static readonly string[] FourteenTracks =
    [
        "01 Preface.txt", "02 I AM part 1.txt", "03 I AM part 2.txt", "04 I AM part 3.txt", "05 Non-dual.txt", "06 Stage 5.txt",
        "07 Stage 6 part 1.txt", "08 Stage 6 part 2.txt", "09 Stage 6 part 3.txt", "10 Stage 6 part 4.txt", "11 Practice.txt",
        "12 Questions.txt", "13 Afterword.txt", "14 Glossary.txt",
    ];
}
