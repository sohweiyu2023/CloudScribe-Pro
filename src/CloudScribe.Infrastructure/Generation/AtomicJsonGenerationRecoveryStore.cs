using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class AtomicJsonGenerationRecoveryStore : IGenerationRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonGenerationRecoveryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(GenerationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = PathFor(snapshot.JobId);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GenerationRecoverySnapshot?> ReadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        var path = PathFor(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<GenerationRecoverySnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Recovery snapshot '{path}' was empty or invalid.");
        }
    }

    public async Task<IReadOnlyList<GenerationRecoverySnapshot>> ListRecoverableAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<GenerationRecoverySnapshot>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.generation.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var snapshot = await JsonSerializer.DeserializeAsync<GenerationRecoverySnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Recovery snapshot '{path}' was empty or invalid.");
                if (snapshot.DecideRecovery().Kind != GenerationRecoveryKind.None)
                {
                    snapshots.Add(snapshot);
                }
            }
        }

        return snapshots
            .OrderByDescending(static snapshot => snapshot.Priority)
            .ThenBy(static snapshot => snapshot.UpdatedAtUnixMilliseconds)
            .ThenBy(static snapshot => snapshot.JobId)
            .ToArray();
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        File.Delete(PathFor(jobId));
        return Task.CompletedTask;
    }

    private string PathFor(Guid jobId) => Path.Combine(_directory, jobId.ToString("N") + ".generation.json");
}
