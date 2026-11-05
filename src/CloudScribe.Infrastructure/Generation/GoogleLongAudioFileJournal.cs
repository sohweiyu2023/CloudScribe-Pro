using System.Text.Json;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private, single-machine POST claim journal. No raw input, credentials or provider prose are stored.</summary>
public sealed partial class GoogleLongAudioFileJournal
{
    private readonly string _root;

    public GoogleLongAudioFileJournal(string privateDirectory)
    {
        _root = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(privateDirectory, "Long Audio journal requires a physical private directory.");
    }

    public GoogleLongAudioCheckpoint Load(Guid id)
    {
        using var lease = Lease(id);
        return Read(id);
    }

    internal bool TryClaim(GoogleLongAudioRequest request)
    {
        using var lease = Lease(request.AttemptId);
        string path = PathFor(request.AttemptId);
        if (File.Exists(path)) return false;
        using var submission = SubmissionLease();
        BeginObservation(request.AttemptId, allowExisting: false);
        var value = new GoogleLongAudioCheckpoint(request.AttemptId, request.BindingSha256, request.ProjectNumber,
            request.Location, request.CredentialReferenceId, request.OutputGcsUri, request.PayloadSha256, request.PayloadBytes,
            GoogleLongAudioState.PostClaimed, null, 0, null, 0);
        WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value));
        return true;
    }

    internal GoogleLongAudioCheckpoint Save(GoogleLongAudioCheckpoint expected, GoogleLongAudioCheckpoint updated)
    {
        using var lease = Lease(expected.AttemptId);
        if (Read(expected.AttemptId) != expected) throw new InvalidOperationException("Long Audio journal changed; no request may be repeated.");
        if (updated with
        {
            State = expected.State,
            OperationName = expected.OperationName,
            HttpStatus = expected.HttpStatus,
            RpcCode = expected.RpcCode,
            Revision = expected.Revision,
            Observation = expected.Observation,
            OperationParseFailed = expected.OperationParseFailed
        } != expected || updated.Revision != expected.Revision + 1)
            throw new InvalidOperationException("Long Audio journal binding cannot change.");
        string path = PathFor(expected.AttemptId), pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            WriteNew(pending, JsonSerializer.SerializeToUtf8Bytes(updated));
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return updated;
    }

    private GoogleLongAudioCheckpoint Read(Guid id)
    {
        string path = PathFor(id);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 8192) throw new InvalidDataException("Invalid Long Audio journal extent.");
        byte[] bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        GoogleLongAudioValidation.RequireUnique(json.RootElement);
        var item = JsonSerializer.Deserialize<GoogleLongAudioCheckpoint>(bytes) ?? throw new InvalidDataException("Missing Long Audio journal.");
        GoogleLongAudioValidation.RequireScope(item.AttemptId, item.ProjectNumber, item.Location, item.CredentialReferenceId, item.OutputGcsUri);
        if (item.AttemptId != id || !Hash(item.BindingSha256) || !Hash(item.PayloadSha256) || item.PayloadBytes is < 1 or > 1_000_000
            || !string.Equals(item.BindingSha256, GoogleLongAudioValidation.BindingDigest(item.AttemptId, item.ProjectNumber,
                item.Location, item.CredentialReferenceId, item.OutputGcsUri, item.PayloadSha256), StringComparison.Ordinal)
            || !Enum.IsDefined(item.State) || item.Revision < 0 || item.HttpStatus is < 0 or > 599 || item.RpcCode is < 0 or > 16
            || item.State is GoogleLongAudioState.Pollable or GoogleLongAudioState.CompletedUnverified
                && !GoogleLongAudioValidation.IsOperation(item.OperationName, item.ProjectNumber, item.Location)
            || item.OperationName is not null && !GoogleLongAudioValidation.IsOperation(item.OperationName, item.ProjectNumber, item.Location))
            throw new InvalidDataException("Long Audio journal identity is invalid.");
        if (item.Observation is { } observation && (!Enum.IsDefined(observation.ReadState) || !Enum.IsDefined(observation.ParseState)
            || !Enum.IsDefined(observation.ErrorStatus) || !Enum.IsDefined(observation.Retention) || observation.ObservedBytes is < 0 or > 16_385
            || observation.PrivateRecordId == Guid.Empty || observation.ReadbackVerified && (observation.PrivateRecordId is null
                || observation.Retention != CloudScribe.Domain.Generation.GoogleExplanationRetentionState.SavedPrivately)))
            throw new InvalidDataException("Invalid Long Audio observation metadata.");
        return item;
    }

    internal GoogleLongAudioCheckpoint? ReadIfExists(Guid id)
    {
        using var lease = Lease(id);
        return File.Exists(PathFor(id)) ? Read(id) : null;
    }

    private FileStream Lease(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty attempt is required.", nameof(id));
        _ = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(_root, "Long Audio journal directory changed.");
        string path = Path.Combine(_root, id.ToString("N") + ".lock");
        RequirePhysical(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string PathFor(Guid id)
    {
        string path = Path.Combine(_root, id.ToString("N") + ".json");
        RequirePhysical(path);
        return path;
    }

    private static void RequirePhysical(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Long Audio journal does not follow links.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
