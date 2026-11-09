using System.Globalization;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ArticleAudioExportService
{
    public async Task ExportAsync(Guid parentId, string destination, TrustedMediaToolSelection approved,
        CancellationToken cancellationToken = default)
    {
        using var lease = await TrustedMediaToolsStore.AcquireVerifiedLeaseAsync(approved).ConfigureAwait(false);
        await ExportAsync(parentId, destination, approved.EncoderPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(Guid parentId, string destination, string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _exportInFlight, 1, 0) != 0)
            throw new InvalidOperationException("An article export is already running.");
        string? temporary = null;
        try
        {
            var resolved = PreflightExport(destination, ffmpegPath);
            destination = resolved.Destination;
            ffmpegPath = resolved.Ffmpeg;
            string ffprobePath = resolved.Ffprobe;
            var inspection = await InspectForAssemblyAsync(parentId, cancellationToken).ConfigureAwait(false);
            var ordered = inspection.Ordered;
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".cloudscribe-article-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var segments = await SnapshotInputsAsync(ordered, ffprobePath, temporary, cancellationToken).ConfigureAwait(false);
            TimeSpan total = TimeSpan.FromTicks(segments.Sum(x => x.MeasuredDuration.Ticks));
            var validated = new AudioAssemblyPlan(segments, new GenerationMasteringProfile("article-continuous", 0, null, 0, 0),
                ReleaseAudioFormat.Mp3, total + TimeSpan.FromTicks(1), temporary, "article");
            if (validated.Parts.Count != 1) throw new InvalidOperationException("Article export must produce one continuous file.");
            string resultPath = await AssembleSnapshotsAsync(validated.Segments.Count, temporary, ffmpegPath, cancellationToken).ConfigureAwait(false);
            var finalProbe = await ProbeAsync(ffprobePath, resultPath, temporary, cancellationToken).ConfigureAwait(false);
            if (Math.Abs((finalProbe.Duration - total).TotalSeconds) > 1 + segments.Count * 0.1)
                throw new InvalidOperationException("Continuous output duration does not match its complete ordered inputs.");
            var decode = await native.RunAsync(new(ffmpegPath,
                ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-i", resultPath, "-f", "null", "-"],
                temporary, TimeSpan.FromMinutes(30), 4096), cancellationToken).ConfigureAwait(false);
            if (!decode.Succeeded) throw new InvalidOperationException("Continuous MP3 failed local decoding verification.");
            var current = await InspectForAssemblyAsync(parentId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Fingerprint, inspection.Fingerprint, StringComparison.Ordinal)
                || !current.Ordered.SequenceEqual(ordered))
                throw new InvalidOperationException("Article evidence changed during export.");
            foreach (var receipt in ordered) await VerifyQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            File.Move(resultPath, destination, overwrite: false);
        }
        finally
        {
            if (temporary is not null)
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Volatile.Write(ref _exportInFlight, 0);
        }
    }

    private async Task<string> AssembleSnapshotsAsync(int count, string temporary, string ffmpegPath, CancellationToken token)
    {
        var assembly = AudioAssemblyNativePlanner.PlanArticleSnapshots(count, temporary, ffmpegPath);
        await File.WriteAllTextAsync(Path.Combine(temporary, "article-inputs.ffconcat"), assembly.Manifest,
            new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
        var assembled = await native.RunAsync(assembly.Invocation, token).ConfigureAwait(false);
        if (!assembled.Succeeded) throw new InvalidOperationException("Continuous MP3 assembly failed.");
        string resultPath = assembly.Invocation.Arguments[^1];
        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length is < 12 or > 512L * 1024 * 1024)
            throw new InvalidDataException("Continuous MP3 is missing or outside its supported size bound.");
        byte[] prefix = new byte[12];
        using (var output = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await output.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        if (!(prefix[0] == 'I' && prefix[1] == 'D' && prefix[2] == '3')
            && !(prefix[0] == 0xFF && (prefix[1] & 0xE0) == 0xE0))
            throw new InvalidDataException("Continuous media does not have an MP3 container signature.");
        return resultPath;
    }

    /// <summary>Read-only local path checks. Repeated by export because preflight cannot prevent later filesystem changes.</summary>
    public static (string Destination, string Ffmpeg, string Ffprobe) PreflightExport(string destination, string ffmpegPath)
    {
        destination = PreflightDestination(destination);
        ffmpegPath = Path.GetFullPath(ffmpegPath);
        string ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        if (!string.Equals(Path.GetFileName(ffmpegPath), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            throw new InvalidOperationException("Select an existing FFmpeg installation with ffprobe.exe alongside it.");
        RejectLinks(ffmpegPath);
        RejectLinks(ffprobePath);
        return (destination, ffmpegPath, ffprobePath);
    }

    private async Task<List<AudioSegmentArtifact>> SnapshotInputsAsync(IReadOnlyList<ArticleAcceptedAudio> ordered,
        string ffprobePath, string temporary, CancellationToken cancellationToken)
    {
        List<AudioSegmentArtifact> segments = [];
        int? sampleRate = null, channels = null;
        foreach (var receipt in ordered)
        {
            await VerifyQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
            RejectLinks(receipt.AudioPath);
            if (receipt.AudioBytes is < 12 or > 256L * 1024 * 1024 || new FileInfo(receipt.AudioPath).Length != receipt.AudioBytes)
                throw new InvalidOperationException("Accepted audio is missing or has changed.");
            string copy = Path.Combine(temporary, $"input-{segments.Count + 1:D3}.mp3");
            // Private snapshot prevents later file changes from changing encoder input.
            using (var source = new FileStream(receipt.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            byte[] bytes = await File.ReadAllBytesAsync(copy, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != receipt.AudioBytes || !Same(Hash(bytes), receipt.AudioSha256))
                throw new InvalidOperationException("Accepted audio integrity failed.");
            var probe = await ProbeAsync(ffprobePath, copy, temporary, cancellationToken).ConfigureAwait(false);
            sampleRate ??= probe.Rate;
            channels ??= probe.Channels;
            if (sampleRate != probe.Rate || channels != probe.Channels)
                throw new InvalidOperationException("Article audio formats differ; no continuous export was created.");
            segments.Add(new(receipt.LocalAttemptId, copy, "audio/mpeg", probe.Duration, receipt.AudioSha256));
        }
        return segments;
    }

    private async Task<(TimeSpan Duration, int Rate, int Channels)> ProbeAsync(string executable,
        string file, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await native.RunAsync(new(executable,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name,sample_rate,channels:format=duration", "-of", "json", file],
            workingDirectory, TimeSpan.FromMinutes(2), 16384), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidOperationException("Local MP3 inspection failed.");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1 || !Same(streams[0].GetProperty("codec_name").GetString(), "mp3"))
            throw new InvalidOperationException("Article output requires one verified MP3 audio stream per part.");
        double duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        int rate = int.Parse(streams[0].GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
        int channels = streams[0].GetProperty("channels").GetInt32();
        if (!double.IsFinite(duration) || duration <= 0 || duration > 86400 || rate is < 8000 or > 192000 || channels is < 1 or > 2)
            throw new InvalidOperationException("Article audio measurements are outside supported bounds.");
        return (TimeSpan.FromSeconds(duration), rate, channels);
    }

    private async Task<AssemblyInspection> InspectForAssemblyAsync(Guid parentId, CancellationToken token)
    {
        if (chirpArticles is not null && await chirpArticles.HasStoredPlanAsync(parentId, token).ConfigureAwait(false))
        {
            var article = await chirpArticles.InspectCompleteAsync(parentId, token).ConfigureAwait(false);
            return new(ChirpPolicyValidation.Hash(article.Plan), article.Ordered);
        }
        var legacy = await InspectAsync(parentId, token).ConfigureAwait(false);
        return new(ChirpPolicyValidation.Hash(legacy.Manifest), legacy.Manifest.RequireComplete(legacy.Audio));
    }

    private sealed record AssemblyInspection(string Fingerprint, IReadOnlyList<ArticleAcceptedAudio> Ordered);
}
