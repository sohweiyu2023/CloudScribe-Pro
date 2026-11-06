using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    private async Task SaveReceiptAsync(GoogleLongAudioDocumentReview review, GoogleLongAudioVerifiedArtifact artifact, CancellationToken token)
    {
        var context = await RequireOutputContextAsync(review, token).ConfigureAwait(false);
        string path = Path.Combine(context.OutputDirectory, review.Request.AttemptId.ToString("N") + ".verified.json");
        await VerifyArtifactAsync(review, context, artifact, token).ConfigureAwait(false);
        if (File.Exists(path))
        {
            var existing = ChirpPolicyFiles.Parse<GoogleLongAudioDocumentReceipt>(await ChirpPolicyFiles.ReadAsync(path, 16384, token).ConfigureAwait(false));
            if (existing.Artifact != artifact || !string.Equals(existing.RequestBindingSha256, review.Request.BindingSha256, StringComparison.Ordinal)
                || existing.DocumentId != review.DocumentId || existing.RevisionId != review.RevisionId)
                throw new InvalidDataException("An existing Long Audio receipt differs; preserve it for reconciliation.");
            return;
        }
        var receipt = new GoogleLongAudioDocumentReceipt(1, review.DocumentId, review.RevisionId, review.SourceSha256,
            review.Request.BindingSha256, clock.GetUtcNow(), artifact);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        ChirpPolicyFiles.RejectLinks(path); ChirpPolicyFiles.RejectLinks(pending);
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(JsonSerializer.SerializeToUtf8Bytes(receipt)); stream.Flush(flushToDisk: true); }
            File.Move(pending, path, overwrite: false);
        }
        finally { if (File.Exists(pending)) { ChirpPolicyFiles.RejectLinks(pending); File.Delete(pending); } }
    }

    public async Task<GoogleLongAudioVerifiedArtifact?> InspectOutputAsync(GoogleLongAudioDocumentReview review, CancellationToken token = default)
    {
        var context = await RequireOutputContextAsync(review, token).ConfigureAwait(false);
        string path = Path.Combine(context.OutputDirectory, review.Request.AttemptId.ToString("N") + ".verified.json");
        if (!File.Exists(path)) return null;
        var receipt = ChirpPolicyFiles.Parse<GoogleLongAudioDocumentReceipt>(await ChirpPolicyFiles.ReadAsync(path, 16384, token).ConfigureAwait(false));
        if (receipt.Version != 1 || receipt.DocumentId != review.DocumentId || receipt.RevisionId != review.RevisionId
            || !string.Equals(receipt.SourceSha256, review.SourceSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.RequestBindingSha256, review.Request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The saved Long Audio output belongs to a different source or request.");
        await VerifyArtifactAsync(review, context, receipt.Artifact, token).ConfigureAwait(false);
        return receipt.Artifact;
    }

    private async Task<InstalledGoogleLongAudioContext> RequireOutputContextAsync(GoogleLongAudioDocumentReview review, CancellationToken token)
    {
        var context = await installed.LoadVerifiedAsync(token).ConfigureAwait(false);
        if (context is null || !string.Equals(context.ScopeSha256, review.ScopeSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Long Audio scope changed; preserve the output and history.");
        return context;
    }

    private async Task VerifyArtifactAsync(GoogleLongAudioDocumentReview review, InstalledGoogleLongAudioContext context,
        GoogleLongAudioVerifiedArtifact artifact, CancellationToken token)
    {
        var admission = new GoogleLongAudioLedgerAdmission(context.ParentPolicyPath, context.ParentPolicySha256,
            context.ScopePath, context.ScopeSha256, context.LedgerPath, (_, _, _) => ValueTask.CompletedTask, clock);
        await admission.RequireExistingCommitmentAsync(review.Request, token).ConfigureAwait(false);
        string extension = string.Equals(review.Request.AudioEncoding, "MP3", StringComparison.Ordinal) ? ".mp3" : ".wav";
        if (!Directory.Exists(context.OperationsDirectory)) throw new InvalidDataException("The original Long Audio operation journal is missing.");
        var checkpoint = new GoogleLongAudioFileJournal(context.OperationsDirectory).Load(review.Request.AttemptId);
        if (checkpoint.State != GoogleLongAudioState.CompletedUnverified || checkpoint.Observation is not { ReadbackVerified: true }
            || !string.Equals(checkpoint.BindingSha256, review.Request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The original verified Long Audio operation differs from the saved file.");
        string expected = Path.Combine(context.OutputDirectory, review.Request.AttemptId.ToString("N") + extension);
        if (!string.Equals(artifact.LocalPath, expected, StringComparison.Ordinal)
            || !string.Equals(artifact.Identity.BindingSha256, review.Request.BindingSha256, StringComparison.Ordinal)
            || artifact.Bytes != artifact.Identity.Bytes || artifact.Bytes < 12 || artifact.Bytes > review.Job.MaximumOutputBytes)
            throw new InvalidDataException("Long Audio file provenance differs from its exact configured job.");
        ChirpPolicyFiles.RejectLinks(expected);
        using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length != artifact.Bytes || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), artifact.AudioSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The saved Long Audio file changed. Repair locally; do not regenerate accepted speech.");
    }
}
