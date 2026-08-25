using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class AtomicJsonGenerationSegmentProgressStore : IGenerationSegmentProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonGenerationSegmentProgressStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(GenerationSegmentProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = PathFor(progress.JobId, progress.SegmentId);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, progress, JsonOptions, cancellationToken).ConfigureAwait(false);
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

    public async Task<GenerationSegmentProgress?> ReadAsync(Guid jobId, string segmentId, CancellationToken cancellationToken = default)
    {
        ValidateJobAndSegment(jobId, segmentId);
        var path = PathFor(jobId, segmentId);
        if (!File.Exists(path))
        {
            return null;
        }

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<GenerationSegmentProgress>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Segment progress '{path}' was empty or invalid.");
        }
    }

    public async Task<IReadOnlyList<GenerationSegmentProgress>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        var prefix = jobId.ToString("N") + "-";
        var items = new List<GenerationSegmentProgress>();
        foreach (var path in Directory.EnumerateFiles(_directory, prefix + "*.segment.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                items.Add(await JsonSerializer.DeserializeAsync<GenerationSegmentProgress>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Segment progress '{path}' was empty or invalid."));
            }
        }

        return items.OrderBy(static item => item.SegmentIndex).ThenBy(static item => item.SegmentId, StringComparer.Ordinal).ToArray();
    }

    public Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        var prefix = jobId.ToString("N") + "-";
        foreach (var path in Directory.EnumerateFiles(_directory, prefix + "*.segment.json", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(Guid jobId, string segmentId)
    {
        ValidateJobAndSegment(jobId, segmentId);
        var safeSegmentId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(segmentId))).ToLowerInvariant();
        return Path.Combine(_directory, jobId.ToString("N") + "-" + safeSegmentId + ".segment.json");
    }

    private static void ValidateJobAndSegment(Guid jobId, string segmentId)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
    }
}
