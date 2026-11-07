using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independent atomic timing file. Never changes the financial ledger, audio or operation journals.</summary>
public sealed class GoogleLongAudioTimingStore(string path, string binding)
{
    public async Task<GoogleLongAudioTiming?> ReadAsync(CancellationToken token = default)
    {
        ChirpPolicyFiles.RejectLinks(path);
        if (!File.Exists(path)) return null;
        var item = ChirpPolicyFiles.Parse<GoogleLongAudioTiming>(await ChirpPolicyFiles.ReadAsync(path, 16384, token).ConfigureAwait(false));
        if (item.Version != 1 || !string.Equals(item.RootBindingSha256, binding, StringComparison.Ordinal)
            || item.RecordedActiveMilliseconds < 0 || item.FirstObservedUtc == default
            || item.FinishedUtc < item.FirstObservedUtc || !Enum.IsDefined(item.LastStage))
            throw new InvalidDataException("Saved timing is invalid; audio and financial history are unchanged.");
        return item;
    }

    public async Task<GoogleLongAudioTiming> BeginAsync(DateTimeOffset now, bool priorCommitment, CancellationToken token = default)
    {
        var previous = await ReadAsync(token).ConfigureAwait(false);
        if (previous?.FinishedUtc is not null) return previous;
        var next = previous is null
            ? new GoogleLongAudioTiming(1, binding, now, null, 0, priorCommitment, false, true, LongAudioWorkflowStage.LocalSetup)
            : previous with { FinishedUtc = null, Running = true, ActiveTimeIncomplete = previous.ActiveTimeIncomplete || previous.Running };
        await SaveAsync(next, token).ConfigureAwait(false);
        return next;
    }

    public async Task<GoogleLongAudioTiming> EndAsync(GoogleLongAudioTiming started, DateTimeOffset now, TimeSpan active,
        LongAudioWorkflowStage stage, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentOutOfRangeException.ThrowIfLessThan(active, TimeSpan.Zero);
        if (started.FinishedUtc is not null) return started;
        var next = started with
        {
            RecordedActiveMilliseconds = checked(started.RecordedActiveMilliseconds + (long)active.TotalMilliseconds),
            Running = false,
            LastStage = stage,
            FinishedUtc = stage == LongAudioWorkflowStage.Completed ? (now >= started.FirstObservedUtc ? now : started.FirstObservedUtc) : null,
            ActiveTimeIncomplete = started.ActiveTimeIncomplete || now < started.FirstObservedUtc
        };
        await SaveAsync(next, token).ConfigureAwait(false);
        return next;
    }

    private async Task SaveAsync(GoogleLongAudioTiming value, CancellationToken token)
    {
        ChirpPolicyFiles.RejectLinks(path);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        ChirpPolicyFiles.RejectLinks(pending);
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            ChirpPolicyFiles.RejectLinks(path);
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) { ChirpPolicyFiles.RejectLinks(pending); File.Delete(pending); } }
    }
}
