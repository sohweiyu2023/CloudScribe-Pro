namespace CloudScribe.App.Composition;

/// <summary>Local output names and safe, actionable export guidance.</summary>
public static class AudioOutputPresentation
{
    public static string SuggestedFileName(string? title, string extension)
    {
        if (extension is not (".mp3" or ".wav")) throw new ArgumentException("Unsupported audio extension.", nameof(extension));
        string name = string.Concat((title ?? string.Empty).Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "complete-article";
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM0", "LPT0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        // Windows filesystem component limit, not an article-content limit.
        int maximum = 255 - extension.Length;
        if (name.Length > maximum)
        {
            int length = char.IsHighSurrogate(name[maximum - 1]) ? maximum - 1 : maximum;
            name = name[..length].TrimEnd(' ', '.');
        }
        return name + extension;
    }

    public static string RecoveryAdvice(Exception error) => error switch
    {
        UnauthorizedAccessException => "Choose a folder you can write to and check whether another app has locked the output.",
        PathTooLongException => "Choose a shorter filename or a folder closer to the drive root.",
        FileNotFoundException or DirectoryNotFoundException => "The audio file or folder is unavailable. Restore the saved location or reconnect its drive before exporting.",
        InvalidDataException => "Saved audio or its verification record could not be validated. Keep the original files and inspect diagnostics; do not regenerate speech to fix an export problem.",
        IOException => "Check free disk space, the destination folder and whether the filename already exists. Choose a new filename; existing files are preserved.",
        _ => "Check audio setup and choose a new writable audio filename. Inspect the diagnostic reference below if the problem continues.",
    };
}
