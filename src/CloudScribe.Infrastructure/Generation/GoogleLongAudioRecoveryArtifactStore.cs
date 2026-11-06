using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Persists output-boundary artifacts without impersonating a whole-document or synchronous provider receipt.</summary>
public sealed class GoogleLongAudioRecoveryArtifactStore
{
    private readonly string output;
    private readonly GoogleLongAudioFileJournal journal;
    public GoogleLongAudioRecoveryArtifactStore(string outputDirectory, GoogleLongAudioFileJournal operations)
    {
        output = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(outputDirectory, "Recovery artifacts require a physical output directory.");
        journal = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async Task RetainAsync(GoogleLongAudioRequest root, GoogleLongAudioRequest request,
        GoogleLongAudioVerifiedArtifact artifact, CancellationToken token = default)
    {
        var receipt = new GoogleLongAudioRecoveryArtifactReceipt(1, root.AttemptId, root.BindingSha256, artifact);
        await VerifyAsync(request.AttemptId, request.BindingSha256, receipt, token).ConfigureAwait(false);
        string path = ReceiptPath(request.AttemptId); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (bytes.Length > 16_384) throw new InvalidDataException("Recovery receipt metadata exceeds its readable envelope. Preserve the verified audio.");
        if (File.Exists(path))
        {
            byte[] existing = await ChirpPolicyFiles.ReadAsync(path, 16_384, token).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Recovery artifact receipt already exists with different evidence.");
        }
        else
        {
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending"; ChirpPolicyFiles.RejectLinks(pending);
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await file.WriteAsync(bytes, token).ConfigureAwait(false); file.Flush(flushToDisk: true); }
                ChirpPolicyFiles.RejectLinks(path); File.Move(pending, path, overwrite: false);
            }
            finally { if (File.Exists(pending)) { ChirpPolicyFiles.RejectLinks(pending); File.Delete(pending); } }
        }
        if (!await HasVerifiedAsync(request.AttemptId, request.BindingSha256, token).ConfigureAwait(false))
            throw new InvalidDataException("Recovery artifact receipt could not be read back. Preserve the output; do not regenerate.");
    }

    public async Task<bool> HasVerifiedAsync(Guid attempt, string bindingSha256, CancellationToken token = default)
    {
        string path = ReceiptPath(attempt);
        if (!File.Exists(path)) return false;
        var receipt = ChirpPolicyFiles.Parse<GoogleLongAudioRecoveryArtifactReceipt>(await ChirpPolicyFiles.ReadAsync(path, 16_384, token).ConfigureAwait(false));
        await VerifyAsync(attempt, bindingSha256, receipt, token).ConfigureAwait(false); return true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "GCS transport MD5 compatibility only; SHA256 independently binds the artifact.")]
    private async Task VerifyAsync(Guid attempt, string binding, GoogleLongAudioRecoveryArtifactReceipt receipt, CancellationToken token)
    {
        if (receipt.Artifact is not { Identity: not null } artifact) throw new InvalidDataException("Recovery artifact metadata is missing.");
        var state = journal.Load(attempt); var root = journal.Load(receipt.RootAttemptId); var identity = artifact.Identity;
        if (receipt.Version != 1 || !string.Equals(root.BindingSha256, receipt.RootBindingSha256, StringComparison.Ordinal)
            || root.State is not (GoogleLongAudioState.Failed or GoogleLongAudioState.CompletedUnverified)
            || root.OperationParseFailed || root.Observation is not { ReadbackVerified: true }
            || root.State == GoogleLongAudioState.Failed && root.FailureKind != GoogleLongAudioFailureKind.SentenceLengthReported
            || state.State != GoogleLongAudioState.CompletedUnverified || state.Observation is not { ReadbackVerified: true }
            || !string.Equals(state.BindingSha256, binding, StringComparison.Ordinal) || !string.Equals(identity.BindingSha256, binding, StringComparison.Ordinal)
            || !string.Equals("gs://" + identity.Bucket + "/" + identity.ObjectName, state.OutputGcsUri, StringComparison.Ordinal)
            || artifact.Bytes < 12 || artifact.Bytes != identity.Bytes || !string.Equals(artifact.LocalPath, AudioPath(attempt), StringComparison.Ordinal))
            throw new InvalidDataException("Recovery artifact provenance does not match the retained exact operation.");
        string identityPath = Path.Combine(output, attempt.ToString("N") + ".object.json");
        var pinned = ChirpPolicyFiles.Parse<GoogleLongAudioObjectIdentity>(await ChirpPolicyFiles.ReadAsync(identityPath, 8192, token).ConfigureAwait(false));
        if (pinned != identity || !long.TryParse(identity.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation <= 0
            || !string.Equals(identity.Generation, generation.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || identity.Md5Base64.Length != 24 || Convert.FromBase64String(identity.Md5Base64).Length != 16)
            throw new InvalidDataException("Recovery artifact storage identity changed.");
        string audio = AudioPath(attempt); ChirpPolicyFiles.RejectLinks(audio);
        using var input = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != artifact.Bytes || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)), artifact.AudioSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery audio bytes changed; repair locally instead of resending.");
        input.Position = 0;
        if (!string.Equals(Convert.ToBase64String(await MD5.HashDataAsync(input, token).ConfigureAwait(false)), identity.Md5Base64, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery audio storage checksum changed.");
    }

    private string ReceiptPath(Guid id)
    {
        string path = Path.Combine(output, id.ToString("N") + ".recovery-verified.json"); ChirpPolicyFiles.RejectLinks(path); return path;
    }
    private string AudioPath(Guid id) => Path.Combine(output, id.ToString("N") + ".wav");
}
