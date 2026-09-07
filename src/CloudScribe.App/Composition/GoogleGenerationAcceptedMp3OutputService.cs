using System.Diagnostics;
using System.Security.Cryptography;
using CloudScribe.Application.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Persists only a genuinely accepted Stage6 Google response. The accepted provider bytes are
/// written without transformation, read back, and SHA-256 compared before the path is exposed.
/// This is output-integrity handling only; it never creates authorization or reconciliation evidence.
/// </summary>
public sealed class GoogleGenerationAcceptedMp3OutputService
{
    private const string ExpectedContentType = "audio/mpeg";
    private readonly string _outputDirectory;

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
        string path = CreateUniquePath(response.ProviderRequestId);
        byte[] expectedHash = SHA256.HashData(response.MediaBytes.Span);
        try
        {
            await File.WriteAllBytesAsync(path, response.MediaBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            byte[] persisted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] persistedHash = SHA256.HashData(persisted);
            if (persisted.Length != response.MediaBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedHash, persistedHash))
                throw new InvalidOperationException("Persisted Google MP3 bytes differ from the accepted provider bytes.");
            return new GoogleGenerationAcceptedMp3Output(path, persisted.Length, Convert.ToHexString(expectedHash));
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public Task PlayAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string outputRoot = Path.GetFullPath(_outputDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google MP3 playback is restricted to CloudScribe's verified generated-output directory.");
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verified Google MP3 output is no longer available.");
        _ = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Windows could not open the verified Google MP3 output.");
        return Task.CompletedTask;
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
}
