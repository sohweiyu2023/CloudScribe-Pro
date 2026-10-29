using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CloudScribe.App.Diagnostics;
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
    private readonly string[] _verifiedRoots;
    private readonly ConcurrentDictionary<string, AcceptedPlaybackIntegrity> _acceptedPlaybackIntegrity =
        new(StringComparer.OrdinalIgnoreCase);

    public GoogleGenerationAcceptedMp3OutputService()
        : this(GetDefaultOutputDirectory())
    {
    }

    public GoogleGenerationAcceptedMp3OutputService(string outputDirectory, IReadOnlyList<string>? additionalVerifiedRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        if (additionalVerifiedRoots?.Count > 4) throw new ArgumentException("Too many verified local output roots.", nameof(additionalVerifiedRoots));
        _verifiedRoots = new[] { _outputDirectory }.Concat(additionalVerifiedRoots ?? []).Select(Path.GetFullPath).ToArray();
    }

    public static GoogleGenerationAcceptedMp3OutputService WithArticleOutputRoot(string root) => new(GetDefaultOutputDirectory(), [root]);

    /// <summary>Adopts only byte-identical accepted output already durably saved by the article journal. No generation permission is created.</summary>
    public async Task RegisterAcceptedAsync(GoogleGenerationQueueOutcome outcome, string path, CancellationToken cancellationToken = default)
    {
        if (outcome.RequiresReconciliation || outcome.Response is not { IsAccepted: true } response || response.MediaBytes.IsEmpty
            || !string.Equals(response.DiagnosticCode, "google-accepted", StringComparison.Ordinal)
            || !string.Equals(response.MediaContentType, ExpectedContentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted MP3 response evidence is unavailable.");
        string full = Path.GetFullPath(path);
        RequireOutputRoot(full);
        if (new FileInfo(full).Length != response.MediaBytes.Length || response.MediaBytes.Length > 256 * 1024 * 1024)
            throw new InvalidOperationException("Saved MP3 size differs from accepted output.");
        byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(response.MediaBytes.Span)) throw new InvalidOperationException("Saved MP3 differs from accepted provider bytes.");
        _acceptedPlaybackIntegrity[full] = new(bytes.LongLength, SHA256.HashData(bytes));
    }

    private static string GetDefaultOutputDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Windows LocalApplicationData is unavailable for Google MP3 output.");
        return Path.Combine(localAppData, "CloudScribe Pro", "Generated");
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
        try
        {
            NativeUiDiagnosticLog.Action(NativeUiOperation.Play, NativeUiPhase.Open);
            string fullPath = await RequireVerifiedAcceptedFileAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true })
                ?? throw new InvalidOperationException("Windows could not open the verified Google MP3 output.");
            NativeUiDiagnosticLog.Action(NativeUiOperation.Play, NativeUiPhase.ShellOpened);
        }
        catch (OperationCanceledException)
        {
            NativeUiDiagnosticLog.Action(NativeUiOperation.Play, NativeUiPhase.Cancel);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            NativeUiDiagnosticLog.Action(NativeUiOperation.Play, NativeUiPhase.Failed, error);
            throw;
        }
    }

    public Task<string> VerifyAcceptedAsync(string path, CancellationToken cancellationToken = default) =>
        RequireVerifiedAcceptedFileAsync(path, cancellationToken);

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
        RequireOutputRoot(fullPath);
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

    private void RequireOutputRoot(string path)
    {
        if (!_verifiedRoots.Any(root => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Google MP3 access is restricted to configured verified output directories.");
        string? current = path;
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Linked output paths are not supported.");
            current = Path.GetDirectoryName(current);
        }
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
