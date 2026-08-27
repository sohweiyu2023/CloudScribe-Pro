namespace CloudScribe.Domain.Security;

public sealed class ArchiveExtractionPolicy
{
    public ArchiveExtractionPolicy(long maximumEntryBytes, long maximumTotalBytes, int maximumEntries)
    {
        if (maximumEntryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        }
        if (maximumTotalBytes < maximumEntryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        }
        if (maximumEntries is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
        MaximumEntries = maximumEntries;
    }

    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
    public int MaximumEntries { get; }

    public IReadOnlyList<string> ValidateAndResolve(string extractionRoot, IReadOnlyList<ArchiveEntryDescriptor> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionRoot);
        ArgumentNullException.ThrowIfNull(entries);
        if (!Path.IsPathFullyQualified(extractionRoot))
        {
            throw new ArgumentException("Extraction root must be fully qualified.", nameof(extractionRoot));
        }
        if (entries.Count > MaximumEntries)
        {
            throw new InvalidOperationException("Archive entry-count limit exceeded.");
        }

        var root = Path.GetFullPath(extractionRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var resolved = new List<string>(entries.Count);
        long total = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                throw new InvalidOperationException("Archive entry relative path is required.");
            }
            if (entry.IsSymbolicLink)
            {
                throw new InvalidOperationException("Symbolic-link archive entries are not permitted.");
            }
            if (entry.UncompressedLength < 0 || entry.UncompressedLength > MaximumEntryBytes)
            {
                throw new InvalidOperationException("Archive entry size is invalid or exceeds the per-entry limit.");
            }

            checked { total += entry.UncompressedLength; }
            if (total > MaximumTotalBytes)
            {
                throw new InvalidOperationException("Archive total uncompressed-size limit exceeded.");
            }

            var normalizedRelative = entry.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(normalizedRelative))
            {
                throw new InvalidOperationException("Archive entry path must be relative.");
            }

            var destination = Path.GetFullPath(Path.Combine(root, normalizedRelative));
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Archive entry escapes the extraction root.");
            }
            resolved.Add(destination);
        }

        if (resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolved.Count)
        {
            throw new InvalidOperationException("Archive contains colliding output paths.");
        }

        return resolved;
    }
}
