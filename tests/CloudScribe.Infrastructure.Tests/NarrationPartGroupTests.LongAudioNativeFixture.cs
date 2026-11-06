using System.Security.Cryptography;
using System.Text;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    // Optional explicit local acceptance mode; default CI remains fake-native/no external dependency.
    // The transport always stays fake and only this generated tone is passed to real native tools.
    private static async Task<GoogleLongAudioMediaTools> AdaptiveActualToolsAsync(string directory)
    {
        string ffmpeg = Path.GetFullPath(Path.Combine(directory, "ffmpeg.exe"));
        string ffprobe = Path.GetFullPath(Path.Combine(directory, "ffprobe.exe"));
        static async Task<string> Digest(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
        return new(ffmpeg, await Digest(ffmpeg).ConfigureAwait(false), ffprobe, await Digest(ffprobe).ConfigureAwait(false));
    }

    private static byte[] AdaptivePcm()
    {
        const int samples = 1200;
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(1500 * Math.Sin(i * 2 * Math.PI * 440 / 24000)));
        }
        return memory.ToArray();
    }
}
