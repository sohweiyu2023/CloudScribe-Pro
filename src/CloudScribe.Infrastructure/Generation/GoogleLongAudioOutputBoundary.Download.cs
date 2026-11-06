using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioOutputBoundary
{
    private async Task DownloadNewAsync(GoogleLongAudioRequest request, GoogleLongAudioObjectIdentity identity,
        string destination, CancellationToken token)
    {
        RequirePhysical(destination);
        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using var response = await GetAsync(request, ObjectUri(identity.Bucket, identity.ObjectName, identity.Generation, true), token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } declared && declared != identity.Bytes)
                throw new InvalidDataException("Private object declared length changed.");
            using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (var target = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536]; long count = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    count = checked(count + read);
                    if (count > identity.Bytes) throw new InvalidDataException("Private object exceeded its pinned size.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (count != identity.Bytes) throw new InvalidDataException("Private object was incomplete.");
                target.Flush(flushToDisk: true);
            }
            _ = await VerifyBytesAsync(pending, identity, token).ConfigureAwait(false);
            RequirePhysical(destination); File.Move(pending, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(pending)) { RequirePhysical(pending); File.Delete(pending); }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Matches the GCS transport checksum only; exact generation and TLS bind the object, and SHA256 independently identifies retained local bytes. MD5 is not used for authentication.")]
    private static async Task<string> VerifyBytesAsync(string path, GoogleLongAudioObjectIdentity identity, CancellationToken token)
    {
        RequirePhysical(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length != identity.Bytes) throw new InvalidDataException("Local output size differs from its immutable object.");
        byte[] md5 = await MD5.HashDataAsync(file, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(md5, Convert.FromBase64String(identity.Md5Base64)))
            throw new InvalidDataException("Private output checksum verification failed.");
        file.Position = 0;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpContent content, int maximum, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream(); byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return result.ToArray();
            if (result.Length + read > maximum) throw new InvalidDataException("Private object metadata exceeds its parse bound.");
            result.Write(buffer, 0, read);
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximum, CancellationToken token)
    {
        RequirePhysical(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Output identity extent is invalid.");
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new InvalidDataException("Output identity changed during read.");
        return bytes;
    }

    private static void RequirePhysical(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked private output paths are refused.");
    }

    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Ambiguous private output metadata."); RequireUnique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUnique(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }
}
