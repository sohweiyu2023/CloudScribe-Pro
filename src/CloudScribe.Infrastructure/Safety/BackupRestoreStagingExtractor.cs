using System.IO.Compression;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed record BackupRestoreStagingResult(string StagingDirectory, int FilesExtracted, long BytesExtracted);

public static class BackupRestoreStagingExtractor
{
    public static BackupRestoreStagingResult ExtractAdmittedArchive(
        string archivePath,
        string stagingRoot,
        BackupRestoreDecision decision,
        long maximumExtractedBytes = 4L * 1024L * 1024L * 1024L)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.MayRestore || !string.Equals(decision.Reason, "restore-admitted", StringComparison.Ordinal))
            throw new InvalidOperationException($"Restore extraction requires an admitted archive: {decision.Reason}");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractedBytes);

        var root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagingPrefix = staging.EndsWith(Path.DirectorySeparatorChar)
            ? staging
            : staging + Path.DirectorySeparatorChar;

        var files = 0;
        long totalBytes = 0;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (ExtractEntry(entry, staging, stagingPrefix, maximumExtractedBytes, ref totalBytes))
                    files++;
            }

            return new(staging, files, totalBytes);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    private static bool ExtractEntry(
        ZipArchiveEntry entry,
        string staging,
        string stagingPrefix,
        long maximumExtractedBytes,
        ref long totalBytes)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000)
            throw new InvalidDataException("Restore archive contains a symbolic link entry.");

        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(staging, relative));
        if (!destination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore archive entry escapes the staging directory.");

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            return false;
        }

        checked { totalBytes += entry.Length; }
        if (totalBytes > maximumExtractedBytes)
            throw new InvalidDataException("Restore archive exceeds the bounded extracted-size limit.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        if (output.Length != entry.Length)
            throw new InvalidDataException("Restore archive entry length changed during extraction.");
        return true;
    }
}
