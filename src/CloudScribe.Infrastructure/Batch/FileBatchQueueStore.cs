using System.Text.Json;
using System.Text.Json.Serialization;
using CloudScribe.Application.Batch;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Stores each batch as <c>batches/&lt;id&gt;.json</c> in the profile. Saves are atomic (write a temporary
/// file, flush to disk, then replace, keeping the previous good copy as <c>.bak</c>), so an interrupted write never
/// loses the queue. A revision check refuses to overwrite progress saved by another window.</summary>
public sealed class FileBatchQueueStore : IBatchQueueStore, IDisposable
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileBatchQueueStore(string profileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        _directory = Path.Combine(Path.GetFullPath(profileRoot), "batches");
    }

    public string Directory => _directory;

    public void Dispose() => _gate.Dispose();

    public async Task<NarrationBatch?> LoadAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(batchId);
        var primary = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (primary is not null) return primary;
        return await TryReadAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NarrationBatch>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(_directory)) return [];
        var result = new List<NarrationBatch>();
        foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)
                && await LoadAsync(id, cancellationToken).ConfigureAwait(false) is { } queue) result.Add(queue);
        }
        return result.OrderByDescending(x => x.CreatedUtc).ToArray();
    }

    public async Task<NarrationBatch> SaveAsync(NarrationBatch queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            string path = PathFor(queue.BatchId);
            var stored = await TryReadAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (stored is null && File.Exists(path))
            {
                // The main file is damaged (for example a power cut mid-write on an old version). Continue from the good
                // backup only if that is the copy the caller loaded; keep the damaged file beside it, never delete it.
                var backup = await TryReadAsync(path + ".bak", CancellationToken.None).ConfigureAwait(false);
                if (backup is null || backup.Revision != queue.Revision)
                    throw new BatchConcurrencyException("The saved batch could not be read. It was not overwritten.");
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(path, path + ".damaged-" + stamp, overwrite: false);
                stored = backup;
            }
            if (stored is not null && stored.Revision != queue.Revision) throw new BatchConcurrencyException();
            if (stored is null && queue.Revision != 0 && File.Exists(path)) throw new BatchConcurrencyException("The saved batch could not be read. It was not overwritten.");
            var next = queue with { Version = NarrationBatch.CurrentVersion, Revision = queue.Revision + 1 };
            string temporary = path + ".tmp";
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, next, Json, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
            return next;
        }
        finally { _gate.Release(); }
    }

    public IDisposable? TryAcquireRunLock(Guid batchId)
    {
        System.IO.Directory.CreateDirectory(_directory);
        try
        {
            return new FileStream(Path.Combine(_directory, batchId.ToString("N") + ".run.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException) { return null; }
    }

    private string PathFor(Guid batchId) => Path.Combine(_directory, batchId.ToString("N") + ".json");

    private static async Task<NarrationBatch?> TryReadAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaximumFileBytes) return null;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var owned = stream.ConfigureAwait(false);
            var queue = await JsonSerializer.DeserializeAsync<NarrationBatch>(stream, Json, token).ConfigureAwait(false);
            return queue is { Version: NarrationBatch.CurrentVersion, Items: not null, Settings: not null } ? queue : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return null; }
    }
}
