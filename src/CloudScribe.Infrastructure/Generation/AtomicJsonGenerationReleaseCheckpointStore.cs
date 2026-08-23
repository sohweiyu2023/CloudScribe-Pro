using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class AtomicJsonGenerationReleaseCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonGenerationReleaseCheckpointStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(GenerationReleaseCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        Validate(checkpoint);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = PathFor(checkpoint.CollectionId);
            var existing = await ReadCoreAsync(destination, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Revision > checkpoint.Revision)
                    throw new InvalidOperationException("Release checkpoint revision cannot move backwards.");
                if (existing.Revision == checkpoint.Revision && existing.RecordedAtUtc > checkpoint.RecordedAtUtc)
                    throw new InvalidOperationException("Release checkpoint time cannot move backwards.");
                if (existing.State == GenerationReleaseCheckpointState.Finalized && checkpoint.State != GenerationReleaseCheckpointState.Finalized)
                    throw new InvalidOperationException("A finalized release checkpoint cannot regress to pending verification.");
            }

            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<GenerationReleaseCheckpoint?> ReadAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        if (collectionId == Guid.Empty)
            throw new ArgumentException("Collection id is required.", nameof(collectionId));
        return ReadCoreAsync(PathFor(collectionId), cancellationToken);
    }

    private async Task<GenerationReleaseCheckpoint?> ReadCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checkpoint = await JsonSerializer.DeserializeAsync<GenerationReleaseCheckpoint>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Release checkpoint '{path}' was empty or invalid.");
        Validate(checkpoint);
        return checkpoint;
    }

    private string PathFor(Guid collectionId) =>
        Path.Combine(_directory, collectionId.ToString("N") + ".release-checkpoint.json");

    private static void Validate(GenerationReleaseCheckpoint checkpoint)
    {
        if (checkpoint.CollectionId == Guid.Empty)
            throw new InvalidDataException("Release checkpoint collection id is required.");
        if (checkpoint.Revision < 0)
            throw new InvalidDataException("Release checkpoint revision cannot be negative.");
        if (checkpoint.RecordedAtUtc == default)
            throw new InvalidDataException("Release checkpoint timestamp is required.");
        if (checkpoint.ReceiptSha256.Length != 64 || checkpoint.OutputSha256.Length != 64)
            throw new InvalidDataException("Release checkpoint hashes must be SHA-256 hex values.");
        if (!checkpoint.ReceiptSha256.All(Uri.IsHexDigit) || !checkpoint.OutputSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Release checkpoint hashes must contain only hexadecimal characters.");
    }
}
