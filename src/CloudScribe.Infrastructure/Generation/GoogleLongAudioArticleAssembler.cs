using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local PCM concatenation and one final MP3 encode. No provider calls, normalization, compression, tempo,
/// pitch or silence filters; only a fixed safety headroom. Original WAVs and interrupted local work remain recoverable.</summary>
public sealed partial class GoogleLongAudioArticleAssembler(INativeMediaTool native)
{
    /// <summary>Fixed attenuation before MP3 encoding. Google LINEAR16 parts were measured peaking at digital full scale,
    /// and the decoded MP3 then exceeded full scale (+0.35 dBFS, 620 over-range samples in a 74-minute article).
    /// A constant gain keeps relative loudness and joins unchanged; it is not normalization or limiting.</summary>
    public const string EncodeHeadroomFilter = "volume=-1.5dB";

    public static IReadOnlyList<string> FinalEncodeArguments(string manifest, string pending, string? title = null)
    {
        List<string> arguments = ["-nostdin", "-xerror", "-v", "error", "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "1", "-i", manifest,
            "-map", "0:a:0", "-map_metadata", "-1", "-af", EncodeHeadroomFilter, "-c:a", "libmp3lame", "-q:a", "2"];
        string? tag = MetadataTitle(title);
        if (tag is not null) arguments.AddRange(["-id3v2_version", "3", "-metadata", "title=" + tag]);
        arguments.AddRange(["-n", pending]);
        return arguments.AsReadOnly();
    }

    /// <summary>The article title for the MP3 tag, so players show a name instead of a file identifier.
    /// Control characters become spaces and very long titles are shortened; the article text is never included.</summary>
    public static string? MetadataTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string clean = string.Join(' ', new string(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 200) clean = clean[..(char.IsHighSurrogate(clean[199]) ? 199 : 200)];
        return clean.Length == 0 ? null : clean;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One local assembly lease owns immutable snapshots, encoding, full verification and durable receipt; it cannot submit provider requests.")]
    public async Task<GoogleLongAudioArticleOutput> AssembleAsync(GoogleLongAudioDocumentReview review,
        IReadOnlyList<GoogleLongAudioRecoveryOutput> ordered, InstalledGoogleLongAudioContext context,
        GoogleLongAudioMediaTools tools, CancellationToken token = default)
    {
        await RequireToolsAsync(tools, token).ConfigureAwait(false);
        await VerifyInputsAsync(review, ordered, context, token).ConfigureAwait(false);
        string root = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(context.OutputDirectory, "Article output requires a physical directory.");
        string path = Path.Combine(root, review.Request.AttemptId.ToString("N") + ".article.mp3");
        string receiptPath = path + ".verified.json"; ChirpPolicyFiles.RejectLinks(path); ChirpPolicyFiles.RejectLinks(receiptPath);
        ChirpPolicyFiles.RejectLinks(path + ".lock");
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = await InspectAsync(review, context, token).ConfigureAwait(false);
        if (previous is not null) return previous;
        string work = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(path + ".work", "Article snapshots require a physical directory.");
        string intent = Path.Combine(work, "ordered-inputs.json");
        await WriteImmutableAsync(intent, JsonSerializer.SerializeToUtf8Bytes(ordered), token).ConfigureAwait(false);
        double duration = 0; List<string> lines = ["ffconcat version 1.0"];
        for (int i = 0; i < ordered.Count; i++)
        {
            string name = "part-" + i.ToString("D8", CultureInfo.InvariantCulture) + ".wav";
            string snapshot = Path.Combine(work, name);
            await SnapshotAsync(ordered[i].Artifact, snapshot, token).ConfigureAwait(false);
            duration += await ProbeAsync(snapshot, "pcm_s16le", review, tools, work, token).ConfigureAwait(false);
            lines.Add("file '" + name + "'");
        }
        string manifest = Path.Combine(work, "inputs.ffconcat");
        await WriteImmutableAsync(manifest, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"), token).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            string pending = Path.Combine(work, "article.mp3"); ChirpPolicyFiles.RejectLinks(pending);
            if (File.Exists(pending)) File.Delete(pending); // Owned, incomplete local encode only; never source or provider output.
            var result = await native.RunAsync(new(tools.FfmpegPath, FinalEncodeArguments(manifest, pending, review.Title),
                work, TimeSpan.FromMinutes(30), 4096), token).ConfigureAwait(false);
            if (!result.Succeeded) throw new InvalidDataException("Local article MP3 encoding failed; WAVs and provider outcomes are preserved.");
            await VerifyFinalAsync(pending, duration, ordered.Count, review, tools, work, token).ConfigureAwait(false);
            await VerifyInputsAsync(review, ordered, context, token).ConfigureAwait(false);
            for (int i = 0; i < ordered.Count; i++)
                await SnapshotAsync(ordered[i].Artifact, Path.Combine(work, "part-" + i.ToString("D8", CultureInfo.InvariantCulture) + ".wav"), token).ConfigureAwait(false);
            ChirpPolicyFiles.RejectLinks(path); File.Move(pending, path, overwrite: false);
        }
        else await VerifyFinalAsync(path, duration, ordered.Count, review, tools, work, token).ConfigureAwait(false);
        var info = new FileInfo(path);
        var output = new GoogleLongAudioArticleOutput(1, review.DocumentId, review.RevisionId, review.SourceSha256,
            review.Request.BindingSha256, ordered, path, info.Length, await HashFileAsync(path, token).ConfigureAwait(false));
        await WriteImmutableAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(output), token).ConfigureAwait(false);
        var verified = await InspectAsync(review, context, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Article output receipt could not be read back.");
        RemoveVerifiedSnapshots(work, ordered.Count);
        return verified;
    }

    /// <summary>After the final MP3 and its receipt are verified, the per-part WAV snapshot copies are redundant:
    /// the original verified WAVs stay in place and the receipt pins them. Removing the copies roughly halves the
    /// disk used per article. The recipe files stay. Best effort: inaccessible copies remain in place.</summary>
    public static int RemoveVerifiedSnapshots(string work, int verifiedPartCount)
    {
        int removed = 0;
        for (int i = 0; i < verifiedPartCount; i++)
        {
            try
            {
                string snapshot = Path.Combine(work, "part-" + i.ToString("D8", CultureInfo.InvariantCulture) + ".wav");
                ChirpPolicyFiles.RejectLinks(snapshot);
                if (!File.Exists(snapshot)) continue;
                File.Delete(snapshot);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { _ = error; /* Preserve verified completion and inaccessible copies. */ }
        }
        return removed;
    }

    public static async Task<GoogleLongAudioArticleOutput?> InspectAsync(GoogleLongAudioDocumentReview review,
        InstalledGoogleLongAudioContext context, CancellationToken token = default)
    {
        string expected = Path.Combine(context.OutputDirectory, review.Request.AttemptId.ToString("N") + ".article.mp3");
        string path = expected + ".verified.json";
        if (!File.Exists(path)) return null;
        // Derived from the maximum number of nonempty source ranges, not an arbitrary leaf-count stop.
        int extent = checked(review.Request.InputUtf8Bytes * 2048 + 16384);
        var output = ChirpPolicyFiles.Parse<GoogleLongAudioArticleOutput>(await ChirpPolicyFiles.ReadAsync(path, extent, token).ConfigureAwait(false));
        if (output.Version != 1 || output.DocumentId != review.DocumentId || output.RevisionId != review.RevisionId
            || !Same(output.SourceSha256, review.SourceSha256) || !Same(output.RootBindingSha256, review.Request.BindingSha256)
            || !string.Equals(output.Mp3Path, expected, StringComparison.Ordinal) || output.Bytes < 12
            || new FileInfo(expected).Length != output.Bytes || !Same(await HashFileAsync(expected, token).ConfigureAwait(false), output.Sha256))
            throw new InvalidDataException("The saved article output changed; preserve the WAVs and repair locally.");
        await VerifyInputsAsync(review, output.Ordered, context, token).ConfigureAwait(false);
        return output;
    }
}
