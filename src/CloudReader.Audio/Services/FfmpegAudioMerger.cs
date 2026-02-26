using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace CloudReader.Audio.Services;

public sealed class FfmpegAudioMerger
{
    public async Task<string> EnsureFfmpegAsync(string appData, CancellationToken ct)
    {
        var ffmpegDir = Path.Combine(appData, "ffmpeg");
        Directory.CreateDirectory(ffmpegDir);
        FFmpeg.SetExecutablesPath(ffmpegDir);

        if (!File.Exists(Path.Combine(ffmpegDir, "ffmpeg")) &&
            !File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe")))
        {
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDir);
        }

        return ffmpegDir;
    }

    public async Task<string> MergeToMp3Async(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken ct)
    {
        var concatFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(concatFile, wavPaths.Select(x => $"file '{x.Replace("'", "'\\''")}'"), ct);
        var conversion = FFmpeg.Conversions.New()
            .AddParameter($"-f concat -safe 0 -i \"{concatFile}\" -af loudnorm -y \"{outputPath}\"");
        await conversion.Start(ct);
        return outputPath;
    }
}
