using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CloudScribe.Application.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Persists only a genuinely accepted Stage6 Google response. The accepted provider bytes are
/// written without transformation, read back, and SHA-256 compared before the path is exposed.
/// Playback and export revalidate the current file against the exact accepted bytes recorded at
/// persistence time. This is output-integrity handling only; it never creates authorization,
/// pricing, or reconciliation evidence.
/// </summary>
public sealed class GoogleGenerationAcceptedMp3OutputService
{
    private const string ExpectedContentType = "audio/mpeg";
    private readonly string _outputDirectory;
    private readonly ConcurrentDictionary<string, AcceptedPlaybackIntegrity> _acceptedPlaybackIntegrity =
        new(StringComparer.OrdinalIgnoreCase);

    public GoogleGenerationAcceptedMp3OutputService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Windows LocalApplicationData is unavailable for Google MP3 output.");
        _outputDirectory = Path.Combine(localAppData, "CloudScribe Pro", "Generated");
    }

    public async Task<GoogleGenerationAcceptedMp3Output> PersistAcceptedAsync(
        GoogleGenerationQueueOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RequiresReconciliation)
            throw new InvalidOperationException("Google output cannot be exposed while reconciliation is required.");
        var response = outcome.Response
            ?? throw new InvalidOperationException("Google generation completed without a provider response.");
        if (!response.IsAccepted)
            throw new InvalidOperationException("Only an accepted Google generation response may become an output file.");
        if (response.MediaBytes.IsEmpty)
            throw new InvalidOperationException("Accepted Google generation response contains no media bytes.");
        if (!string.Equals(response.MediaContentType, ExpectedContentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted Google generation response is not an MP3 media response.");

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_outputDirectory);
        string path = Path.GetFullPath(CreateUniquePath(response.ProviderRequestId));
        byte[] expectedHash = SHA256.HashData(response.MediaBytes.Span);
        try
        {
            await File.WriteAllBytesAsync(path, response.MediaBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            byte[] persisted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] persistedHash = SHA256.HashData(persisted);
            if (persisted.Length != response.MediaBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedHash, persistedHash))
                throw new InvalidOperationException("Persisted Google MP3 bytes differ from the accepted provider bytes.");

            _acceptedPlaybackIntegrity[path] = new AcceptedPlaybackIntegrity(persisted.LongLength, expectedHash.ToArray());
            return new GoogleGenerationAcceptedMp3Output(path, persisted.Length, Convert.ToHexString(expectedHash));
        }
        catch
        {
            _acceptedPlaybackIntegrity.TryRemove(path, out _);
            TryDelete(path);
            throw;
        }
    }

    public async Task PlayAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = await RequireVerifiedAcceptedFileAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Windows could not open the verified Google MP3 output.");
    }

    public async Task<string> ExportVerifiedAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        string sourceFullPath = await RequireVerifiedAcceptedFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destinationFullPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destinationFullPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verified Google output may only be exported as an .mp3 file.");
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            return destinationFullPath;

        AcceptedPlaybackIntegrity expected = _acceptedPlaybackIntegrity[sourceFullPath];
        string? destinationDirectory = Path.GetDirectoryName(destinationFullPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new InvalidOperationException("MP3 export destination has no valid parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        cancellationToken.ThrowIfCancellationRequested();
        byte[] acceptedBytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken).ConfigureAwait(false);
        VerifyBytes(acceptedBytes, expected, "Verified Google MP3 output changed before export and will not be copied.");

        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.cloudscribe-exporting");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, acceptedBytes, cancellationToken).ConfigureAwait(false);
            byte[] exportedBytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            VerifyBytes(exportedBytes, expected, "Exported Google MP3 bytes differ from the accepted provider bytes.");

            File.Move(temporaryPath, destinationFullPath, overwrite: true);
            byte[] finalBytes = await File.ReadAllBytesAsync(destinationFullPath, cancellationToken).ConfigureAwait(false);
            VerifyBytes(finalBytes, expected, "Final exported Google MP3 bytes differ from the accepted provider bytes.");
            return destinationFullPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task<string> RequireVerifiedAcceptedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string outputRoot = Path.GetFullPath(_outputDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google MP3 access is restricted to CloudScribe's verified generated-output directory.");
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verified Google MP3 output is no longer available.");
        if (!_acceptedPlaybackIntegrity.TryGetValue(fullPath, out AcceptedPlaybackIntegrity? expected))
            throw new InvalidOperationException("Google MP3 access requires accepted-byte integrity evidence from this application session.");

        byte[] currentBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyBytes(currentBytes, expected, "Verified Google MP3 output changed after acceptance and will not be used.");
        }
        catch
        {
            _acceptedPlaybackIntegrity.TryRemove(fullPath, out _);
            throw;
        }
        return fullPath;
    }

    private static void VerifyBytes(
        byte[] bytes,
        AcceptedPlaybackIntegrity expected,
        string failureMessage)
    {
        byte[] currentHash = SHA256.HashData(bytes);
        if (bytes.LongLength != expected.ByteLength || !CryptographicOperations.FixedTimeEquals(currentHash, expected.Sha256))
            throw new InvalidOperationException(failureMessage);
    }

    private string CreateUniquePath(string? providerRequestId)
    {
        string requestToken = string.IsNullOrWhiteSpace(providerRequestId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeFileToken(providerRequestId);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(_outputDirectory, $"google-tts-{timestamp}-{requestToken}.mp3");
    }

    private static string SanitizeFileToken(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Where(character => !invalid.Contains(character)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record AcceptedPlaybackIntegrity(long ByteLength, byte[] Sha256);
}
