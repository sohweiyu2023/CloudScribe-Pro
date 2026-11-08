using System.Text.Json;
using System.Text.Json.Serialization;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Keeps the unsaved batch draft as <c>batches/draft.json</c> in the profile (file names, paths and hashes;
/// never text). Writes are atomic with a <c>.bak</c> copy, so a crash mid-save keeps the previous draft. A damaged
/// draft is set aside as <c>.damaged-&lt;time&gt;</c> instead of being deleted.</summary>
public sealed class FileBatchDraftStore : IBatchDraftStore
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _path;
    private readonly ILogger? _logger;

    public FileBatchDraftStore(string profileRoot, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        _path = Path.Combine(Path.GetFullPath(profileRoot), "batches", "draft.json");
        _logger = logger;
    }

    public string FilePath => _path;

    public async Task<BatchDraft?> LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            var draft = await TryReadAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (draft is not null) return draft;
        }
        if (File.Exists(_path))
        {
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(_path, _path + ".damaged-" + stamp, overwrite: false);
            }
            catch (Exception error) when (BatchDiagnostics.NonFatal(error))
            { BatchDiagnostics.Failure(_logger, null, null, BatchDiagnosticOperation.LoadDraft, error); }
        }
        return null;
    }

    public async Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, draft with { Version = BatchDraft.CurrentVersion }, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, _path);
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            BatchDiagnostics.Failure(_logger, null, null, BatchDiagnosticOperation.SaveDraft, error);
            throw;
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (string candidate in new[] { _path, _path + ".bak", _path + ".tmp" })
                if (File.Exists(candidate)) File.Delete(candidate);
            return Task.CompletedTask;
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            BatchDiagnostics.Failure(_logger, null, null, BatchDiagnosticOperation.DiscardDraft, error);
            throw;
        }
    }

    private async Task<BatchDraft?> TryReadAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return null;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var draft = await JsonSerializer.DeserializeAsync<BatchDraft>(stream, Json, token).ConfigureAwait(false);
                return draft is { Version: BatchDraft.CurrentVersion, Items: not null } ? draft : null;
            }
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            BatchDiagnostics.Failure(_logger, null, null, BatchDiagnosticOperation.LoadDraft, error);
            return null;
        }
    }
}
