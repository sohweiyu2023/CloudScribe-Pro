using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioArticleAssembler
{
    private static async Task VerifyInputsAsync(GoogleLongAudioDocumentReview review,
        IReadOnlyList<GoogleLongAudioRecoveryOutput> ordered, InstalledGoogleLongAudioContext context, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(review.Request.CopyPayload());
        string source = payload.RootElement.GetProperty("input").GetProperty("text").GetString()!;
        if (ordered is null || ordered.Count == 0 || !Same(context.ScopeSha256, review.ScopeSha256))
            throw new InvalidDataException("Complete ordered article evidence is required.");
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(await ChirpPolicyFiles.ReadAsync(context.LedgerPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        if (!Same(ledger.ScopeSha256, context.ScopeSha256)) throw new InvalidDataException("Article ledger identity changed.");
        var store = new GoogleLongAudioRecoveryArtifactStore(context.OutputDirectory, new(context.OperationsDirectory));
        int cursor = 0;
        foreach (var leaf in ordered)
        {
            if (leaf.Range.StartScalar != cursor || leaf.Range.EndScalarExclusive <= cursor
                || leaf.Range.ReusedReceiptSha256 is not null) throw new InvalidDataException("Article coverage has a gap, overlap or unsupported receipt.");
            _ = ChirpNarrationPlanner.Slice(source, leaf.Range);
            var claim = ledger.Claims.SingleOrDefault(x => Same(x.RequestBindingSha256, leaf.Artifact.Identity.BindingSha256))
                ?? throw new InvalidDataException("Verified article input has no retained commitment.");
            if (claim.AttemptId != review.Request.AttemptId && (claim.Recovery is not { } proof
                || !Same(proof.RootBindingSha256, review.Request.BindingSha256) || proof.StartScalar != leaf.Range.StartScalar
                || proof.EndScalarExclusive != leaf.Range.EndScalarExclusive
                || !Same(proof.PlannerVersion, GoogleLongAudioRecoveryPlanner.Version) && !Same(proof.PlannerVersion, GoogleLongAudioExplicitRegeneration.PlannerVersion)))
                throw new InvalidDataException("The retained child does not cover this exact article range.");
            VerifyExplicitCoverage(review, leaf, claim, context);
            if (claim.AttemptId == review.Request.AttemptId && (ordered.Count != 1 || !Same(claim.RequestBindingSha256, review.Request.BindingSha256)))
                throw new InvalidDataException("The successful root cannot be combined with child output.");
            if (!await store.HasVerifiedAsync(claim.AttemptId, claim.RequestBindingSha256, token).ConfigureAwait(false))
                throw new InvalidDataException("Article constituent receipt is absent.");
            var receipt = ChirpPolicyFiles.Parse<GoogleLongAudioRecoveryArtifactReceipt>(await ChirpPolicyFiles.ReadAsync(
                Path.Combine(context.OutputDirectory, claim.AttemptId.ToString("N") + ".recovery-verified.json"), 16384, token).ConfigureAwait(false));
            if (!Same(receipt.RootBindingSha256, review.Request.BindingSha256) || receipt.Artifact != leaf.Artifact)
                throw new InvalidDataException("Article constituent differs from its retained verified receipt.");
            cursor = leaf.Range.EndScalarExclusive;
        }
        if (cursor != source.EnumerateRunes().Count()) throw new InvalidDataException("Article source coverage is incomplete.");
    }

    private static void VerifyExplicitCoverage(GoogleLongAudioDocumentReview review, GoogleLongAudioRecoveryOutput leaf,
        GoogleLongAudioBudgetClaim claim, InstalledGoogleLongAudioContext context)
    {
        var overlaps = context.Scope.DeferredUnknownRanges?.Where(x => Same(x.RootBindingSha256, review.Request.BindingSha256)
            && leaf.Range.StartScalar < x.EndScalarExclusive && x.StartScalar < leaf.Range.EndScalarExclusive).ToArray() ?? [];
        bool explicitProof = Same(claim.Recovery?.PlannerVersion, GoogleLongAudioExplicitRegeneration.PlannerVersion);
        if (!explicitProof && overlaps.Length == 0) return;
        if (overlaps.Length != 1 || overlaps[0].ExplicitRegeneration is null)
            throw new InvalidDataException("Unknown source coverage has no exact approved regeneration.");
        var authority = new GoogleLongAudioRecoveryAuthority(review.Request, context.OperationsDirectory, context.OutputDirectory);
        authority.Deferred = new(review.Request, context.Scope, context.OperationsDirectory, context.OutputDirectory,
            context.LedgerPath, context.ScopeSha256, authority);
        var exact = authority.FindExact(claim.AttemptId, context.Scope);
        if (exact.RegenerationOwner != overlaps[0].AttemptId || claim.AttemptId == overlaps[0].AttemptId
            || exact.Proof != claim.Recovery || exact.Part.Range != leaf.Range || exact.Part.MaximumHoldCents != claim.MaximumUsdCents
            || !Same(exact.Part.Request.BindingSha256, claim.RequestBindingSha256))
            throw new InvalidDataException("The chosen replacement coverage does not match its exact retained provenance.");
        authority.Deferred.RequireReady(exact.Part.Request, newPost: false);
    }

    private async Task<double> ProbeAsync(string path, string codec, GoogleLongAudioDocumentReview review,
        GoogleLongAudioMediaTools tools, string work, CancellationToken token)
    {
        var result = await native.RunAsync(new(tools.FfprobePath,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "stream=codec_type,codec_name,sample_rate,channels:format=duration", "-of", "json", path],
            work, TimeSpan.FromMinutes(30), 16384), token).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidDataException("Local article format inspection failed.");
        using var json = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        ChirpPolicyFiles.RequireUnique(json.RootElement);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("One article audio stream is required.");
        var stream = streams[0];
        if (!Same(stream.GetProperty("codec_name").GetString(), codec) || !Same(stream.GetProperty("codec_type").GetString(), "audio")
            || !Same(stream.GetProperty("sample_rate").GetString(), review.Job.ExpectedSampleRate.ToString(CultureInfo.InvariantCulture))
            || stream.GetProperty("channels").GetInt32() != review.Job.ExpectedChannels
            || !double.TryParse(json.RootElement.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Article audio format does not match the reviewed source output.");
        return duration;
    }

    private async Task VerifyFinalAsync(string path, double expectedDuration, int count, GoogleLongAudioDocumentReview review,
        GoogleLongAudioMediaTools tools, string work, CancellationToken token)
    {
        await RequireToolsAsync(tools, token).ConfigureAwait(false);
        double duration = await ProbeAsync(path, "mp3", review, tools, work, token).ConfigureAwait(false);
        if (Math.Abs(duration - expectedDuration) > 1 + count * 0.1)
            throw new InvalidDataException("Final MP3 duration does not match its complete ordered PCM inputs.");
        var decoded = await native.RunAsync(new(tools.FfmpegPath,
            ["-nostdin", "-xerror", "-v", "error", "-protocol_whitelist", "file,pipe", "-f", "mp3", "-i", path, "-map", "0:a:0", "-f", "null", "-"],
            work, TimeSpan.FromMinutes(30), 4096), token).ConfigureAwait(false);
        if (!decoded.Succeeded) throw new InvalidDataException("Final article MP3 did not fully decode; no provider retry is needed.");
    }

    private static async Task RequireToolsAsync(GoogleLongAudioMediaTools tools, CancellationToken token)
    {
        await GoogleLongAudioOutputBoundary.RequireToolAsync(tools.FfmpegPath, tools.FfmpegSha256, token).ConfigureAwait(false);
        await GoogleLongAudioOutputBoundary.RequireToolAsync(tools.FfprobePath, tools.FfprobeSha256, token).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        ChirpPolicyFiles.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }

    private static async Task SnapshotAsync(GoogleLongAudioVerifiedArtifact artifact, string target, CancellationToken token)
    {
        ChirpPolicyFiles.RejectLinks(target); ChirpPolicyFiles.RejectLinks(artifact.LocalPath);
        if (!File.Exists(target))
        {
            string pending = target + "." + Guid.NewGuid().ToString("N") + ".pending"; ChirpPolicyFiles.RejectLinks(pending);
            try
            {
                using var input = new FileStream(artifact.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, token).ConfigureAwait(false); output.Flush(flushToDisk: true); }
                if (new FileInfo(pending).Length != artifact.Bytes || !Same(await HashFileAsync(pending, token).ConfigureAwait(false), artifact.AudioSha256))
                    throw new InvalidDataException("Snapshot copy did not preserve verified PCM input.");
                File.Move(pending, target, overwrite: false);
            }
            finally { if (File.Exists(pending)) { ChirpPolicyFiles.RejectLinks(pending); File.Delete(pending); } }
        }
        if (new FileInfo(target).Length != artifact.Bytes || !Same(await HashFileAsync(target, token).ConfigureAwait(false), artifact.AudioSha256))
            throw new InvalidDataException("Local article snapshot differs from verified input; preserve it for inspection.");
    }

    private static async Task WriteImmutableAsync(string path, byte[] bytes, CancellationToken token)
    {
        ChirpPolicyFiles.RejectLinks(path);
        if (File.Exists(path))
        {
            byte[] existing = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(existing))
                throw new InvalidDataException("The saved article assembly recipe changed.");
            return;
        }
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending"; ChirpPolicyFiles.RejectLinks(pending);
        using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await file.WriteAsync(bytes, token).ConfigureAwait(false); file.Flush(flushToDisk: true); }
        File.Move(pending, path, overwrite: false);
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
}
