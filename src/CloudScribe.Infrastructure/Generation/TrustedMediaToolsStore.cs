using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Generation;



/// <summary>Remembers an explicit local tool choice, not publisher trust or permission to spend.</summary>
public sealed class TrustedMediaToolsStore(string profileDirectory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(profileDirectory), "audio-tools.json");

    public async Task<TrustedMediaToolSelection?> ReadVerifiedAsync() => (await InspectAsync().ConfigureAwait(false)).Selection;

    /// <summary>Reads the remembered choice and says why it cannot be reused, so the UI can explain a repeated prompt.</summary>
    public async Task<TrustedMediaToolsInspection> InspectAsync()
    {
        TrustedMediaToolSelection? saved;
        try
        {
            if (!File.Exists(_path)) return new(null, TrustedMediaToolsState.NotChosen, null);
            RequireOrdinaryFile(_path);
            if (new FileInfo(_path).Length > 16384) return new(null, TrustedMediaToolsState.SettingsUnreadable, null);
            saved = JsonSerializer.Deserialize<TrustedMediaToolSelection>(await File.ReadAllTextAsync(_path).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
        { return new(null, TrustedMediaToolsState.SettingsUnreadable, null); }
        if (saved is null || saved.Version != 1 || string.IsNullOrWhiteSpace(saved.EncoderPath))
            return new(null, TrustedMediaToolsState.SettingsUnreadable, null);
        string probe;
        try { probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(saved.EncoderPath))!, "ffprobe.exe"); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(null, TrustedMediaToolsState.SettingsUnreadable, null); }
        if (!File.Exists(saved.EncoderPath) || !File.Exists(probe)) return new(null, TrustedMediaToolsState.Missing, saved.EncoderPath);
        try
        {
            return saved == await CaptureAsync(saved.EncoderPath).ConfigureAwait(false)
                ? new(saved, TrustedMediaToolsState.Ready, saved.EncoderPath)
                : new(null, TrustedMediaToolsState.Changed, saved.EncoderPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return new(null, TrustedMediaToolsState.Changed, saved.EncoderPath); }
    }

    /// <summary>Plain explanation for a remembered choice that cannot be reused; null when nothing needs saying.</summary>
    public static string? Explain(TrustedMediaToolsInspection inspection) => inspection.State switch
    {
        TrustedMediaToolsState.Changed => "Your approved FFmpeg tools have changed since you chose them (for example after an FFmpeg update). Please confirm them again.",
        TrustedMediaToolsState.Missing => "Your approved FFmpeg tools are no longer at the saved location (they may have been moved or uninstalled). Please choose them again.",
        TrustedMediaToolsState.SettingsUnreadable => "Your saved audio-tools setting could not be read. Please choose FFmpeg again.",
        _ => null,
    };

    public static async Task RequireUnchangedAsync(TrustedMediaToolSelection approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved != await CaptureAsync(approved.EncoderPath).ConfigureAwait(false))
            throw new InvalidOperationException("Your approved audio tools changed. Check audio setup again before continuing.");
    }

    public static async Task<IDisposable> AcquireVerifiedLeaseAsync(TrustedMediaToolSelection approved)
    {
        await RequireUnchangedAsync(approved).ConfigureAwait(false);
        var encoder = new FileStream(approved.EncoderPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? probe = null;
        try
        {
            probe = new FileStream(Path.Combine(Path.GetDirectoryName(approved.EncoderPath)!, "ffprobe.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            // Compare again while both handles deny writes/deletion on Windows. Never approve newly observed bytes.
            await RequireUnchangedAsync(approved).ConfigureAwait(false);
            return new MediaToolLease(encoder, probe);
        }
        catch { encoder.Dispose(); probe?.Dispose(); throw; }
    }

    private sealed class MediaToolLease(FileStream encoder, FileStream probe) : IDisposable
    {
        public void Dispose() { encoder.Dispose(); probe.Dispose(); }
    }
    public async Task RememberAsync(TrustedMediaToolSelection approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved != await CaptureAsync(approved.EncoderPath).ConfigureAwait(false))
            throw new InvalidOperationException("Audio tools changed during setup. Review the installation again.");
        string directory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(Path.GetDirectoryName(_path)!, "Audio settings must use a physical local directory.");
        if (File.Exists(_path)) RequireOrdinaryFile(_path);
        string temporary = Path.Combine(directory, $".audio-tools-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(file, approved).ConfigureAwait(false);
            if (File.Exists(_path)) RequireOrdinaryFile(_path);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<TrustedMediaToolSelection> CaptureAsync(string encoderPath)
    {
        if (!Path.IsPathFullyQualified(encoderPath) || !string.Equals(Path.GetFileName(encoderPath), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose the actual ffmpeg.exe and ffprobe.exe binary folder.");
        string full = Path.GetFullPath(encoderPath);
        string? found = ExistingMediaTools.Find([Path.GetDirectoryName(full)]);
        if (!string.Equals(full, found, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Both audio tools must be ordinary local files.");
        string probe = Path.Combine(Path.GetDirectoryName(full)!, "ffprobe.exe");
        foreach (string file in new[] { full, probe })
        {
            string description = FileVersionInfo.GetVersionInfo(file).FileDescription ?? string.Empty;
            if (description.Contains("ShimGen", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This is a Chocolatey shortcut. Choose the actual FFmpeg bin folder, usually under Chocolatey/lib/ffmpeg/tools/ffmpeg/bin, so changes to both audio tools can be detected.");
        }
        return new(1, full, await HashAsync(full).ConfigureAwait(false), await HashAsync(probe).ConfigureAwait(false));
    }

    private static async Task<string> HashAsync(string path)
    {
        RequireOrdinaryFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file).ConfigureAwait(false));
    }

    private static void RequireOrdinaryFile(string path)
    {
        _ = PhysicalDirectoryPolicy.ValidateExistsWithoutLinks(Path.GetDirectoryName(path)!, "Audio settings and tools cannot use linked directories.");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory)))
            throw new InvalidOperationException("Audio settings and tools must be ordinary local files.");
    }
}
