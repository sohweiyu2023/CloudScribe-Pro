using System.Text;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioFileJournal
{
    internal FileStream AcquireResponseLease()
    {
        _ = CloudScribe.Infrastructure.Files.PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(_root, "Long Audio journal directory changed.");
        string path = Path.Combine(_root, "active-response.lock"); RequirePhysical(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal void MarkObservationPending(Guid id)
    {
        using var submission = SubmissionLease();
        BeginObservation(id, allowExisting: true);
    }

    internal void FinishObservation(GoogleLongAudioCheckpoint current)
    {
        if (!current.HasVerifiedPollDeadline && (current.State == GoogleLongAudioState.PostClaimed || current.Observation is not { ReadbackVerified: true } observation
            || observation.ParseState is GoogleLongAudioParseState.Unavailable or GoogleLongAudioParseState.RedactionUnavailable
                or GoogleLongAudioParseState.InvalidUtf8 || observation.ReadState != CloudScribe.Domain.Generation.GoogleResponseReadState.Complete)) return;
        using var submission = SubmissionLease();
        string pending = ObservationPath(current.AttemptId);
        if (!File.Exists(pending) || ReadObservationOwner(pending) != current.AttemptId)
            throw new InvalidDataException("Long Audio observation ownership changed.");
        File.Delete(pending); // Only this exact verified observation releases this adapter's next-POST stop.
    }

    private void BeginObservation(Guid id, bool allowExisting, bool alreadyValidated = false)
    {
        if (!alreadyValidated) deferred?.RequireReady(id);
        string pending = ObservationPath(id);
        if (File.Exists(pending))
        {
            if (allowExisting && ReadObservationOwner(pending) == id) return;
            throw new InvalidOperationException("A prior Long Audio outcome or protected response remains unverified; paid submission is stopped.");
        }
        WriteNew(pending, Encoding.ASCII.GetBytes(id.ToString("N")));
    }

    private string ObservationPath(Guid id)
    {
        string path = Path.Combine(_root, deferred is null ? "observation-pending.guard" : id.ToString("N") + ".observation-pending.guard");
        RequirePhysical(path); return path;
    }

    private static Guid ReadObservationOwner(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != 32) throw new InvalidDataException("Invalid Long Audio observation guard.");
        byte[] bytes = new byte[32]; input.ReadExactly(bytes);
        return Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "N", out var id) && id != Guid.Empty
            ? id : throw new InvalidDataException("Invalid Long Audio observation owner.");
    }

    private FileStream SubmissionLease()
    {
        _ = CloudScribe.Infrastructure.Files.PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(_root, "Long Audio journal directory changed.");
        string path = Path.Combine(_root, "submission.lock"); RequirePhysical(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
