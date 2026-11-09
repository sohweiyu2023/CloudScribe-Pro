using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Local output handling: verified copies that never overwrite, and checks of finished files.</summary>
public static class BatchOutputFiles
{
    /// <summary>Copies a verified file to <paramref name="destination"/>. If the destination already holds exactly the same
    /// bytes it is reused; any other existing file is never overwritten. The copy is written beside the destination and
    /// checked (size and SHA-256) before it is moved into place.</summary>
    public static async Task CopyVerifiedAsync(string source, string sha256, long bytes, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!await MatchesAsync(source, sha256, bytes, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The verified audio changed before it could be copied. It was not copied.");
        if (File.Exists(destination))
        {
            if (await MatchesAsync(destination, sha256, bytes, cancellationToken).ConfigureAwait(false)) return;
            throw new IOException("A different file already has this name in the output folder. It was not overwritten.");
        }
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new IOException("The output folder is not valid.");
        Directory.CreateDirectory(folder);
        // Independent of the destination basename: a legal 255-unit output must not overflow the component limit
        // merely because its verified copy needs a unique temporary sibling.
        string temporary = Path.Combine(folder, ".cloudscribe-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (!await MatchesAsync(temporary, sha256, bytes, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The copied audio did not verify. Nothing was placed in the output folder.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); } // Only our own unverified partial copy is removed.
    }

    /// <summary>True when <paramref name="path"/> exists with exactly this size and SHA-256.</summary>
    public static async Task<bool> MatchesAsync(string? path, string? sha256, long bytes, CancellationToken cancellationToken = default)
    {
        if (path is null || sha256 is null || !File.Exists(path)) return false;
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                if (stream.Length != bytes) return false;
                string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                return string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
