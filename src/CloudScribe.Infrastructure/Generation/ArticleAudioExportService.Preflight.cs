using CloudScribe.Application.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ArticleAudioExportService
{
    /// <summary>Local checks before generation, repeated for each reservation and again by final export.
    /// Does not run tools, create output, grant authority or reconcile an existing request.</summary>
    public static async Task PreflightGenerationAsync(string destination, string ffmpegPath,
        TrustedMediaToolSelection? approved, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (approved is not null)
            {
                await TrustedMediaToolsStore.RequireUnchangedAsync(approved).ConfigureAwait(false);
                if (!string.Equals(Path.GetFullPath(ffmpegPath), approved.EncoderPath, StringComparison.Ordinal))
                    throw new InvalidOperationException("The selected audio tools differ from the approved tools.");
            }
            _ = PreflightExport(destination, ffmpegPath);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.ArticleOutputPreflight); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Read-only destination checks, also used before a no-cost batch rehearsal's writable probe.</summary>
    public static string PreflightDestination(string destination)
    {
        destination = Path.GetFullPath(destination);
        RejectLinks(destination);
        if (!string.Equals(Path.GetExtension(destination), ".mp3", StringComparison.OrdinalIgnoreCase)
            || File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException("Choose a new MP3 filename; existing files are never replaced.");
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Choose an existing export folder.");
        return destination;
    }
}
