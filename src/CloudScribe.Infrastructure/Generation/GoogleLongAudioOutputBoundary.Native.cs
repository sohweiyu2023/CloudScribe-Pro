using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioOutputBoundary
{
    private async Task VerifyNativeAsync(string path, GoogleLongAudioOutputAllowance allowed, CancellationToken token)
    {
        await RequireToolAsync(ffprobe, tools.FfprobeSha256, token).ConfigureAwait(false);
        await RequireToolAsync(ffmpeg, tools.FfmpegSha256, token).ConfigureAwait(false);
        string format = string.Equals(allowed.ExpectedCodec, "mp3", StringComparison.Ordinal) ? "mp3" : "wav";
        var probe = await native.RunAsync(new(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-f", format, "-show_entries",
                "stream=codec_type,codec_name,sample_rate,channels:format=duration", "-of", "json", path], root,
            TimeSpan.FromMinutes(30), 65536), token).ConfigureAwait(false);
        if (!probe.Succeeded) throw new InvalidDataException("Private audio format verification failed.");
        using var json = JsonDocument.Parse(probe.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 }); RequireUnique(json.RootElement);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("Private output must have exactly one audio stream.");
        var stream = streams[0];
        if (!string.Equals(stream.GetProperty("codec_type").GetString(), "audio", StringComparison.Ordinal)
            || !string.Equals(stream.GetProperty("codec_name").GetString(), allowed.ExpectedCodec, StringComparison.Ordinal)
            || !int.TryParse(stream.GetProperty("sample_rate").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int rate)
            || rate != allowed.SampleRate || stream.GetProperty("channels").GetInt32() != allowed.Channels
            || !double.TryParse(json.RootElement.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Private decoded output differs from the independently approved format.");
        var decoded = await native.RunAsync(new(ffmpeg,
            ["-nostdin", "-xerror", "-v", "error", "-protocol_whitelist", "file,pipe", "-f", format, "-i", path, "-map", "0:a:0", "-f", "null", "-"],
            root, TimeSpan.FromMinutes(30), 4096), token).ConfigureAwait(false);
        if (!decoded.Succeeded) throw new InvalidDataException("Private output did not fully decode.");
    }

    internal static async Task RequireToolAsync(string path, string expected, CancellationToken token)
    {
        RequirePhysical(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException("Native verifier identity changed.");
    }
}
