namespace CloudScribe.Domain.Safety;

public sealed record RestoreExecutionPlan(
    string RestoreRoot,
    IReadOnlyList<RestoreExecutionStep> Steps,
    long TotalBytes)
{
    public static RestoreExecutionPlan Create(
        string restoreRoot,
        BackupRestoreManifest manifest,
        long maximumTotalBytes,
        int maximumFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        if (maximumTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        if (maximumFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));

        manifest = manifest.Validate();
        if (manifest.Files.Count > maximumFiles)
            throw new InvalidOperationException("Backup restore exceeds the authorized file-count ceiling.");

        var root = Path.GetFullPath(restoreRoot);
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Restore root must resolve to an absolute path.");

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var steps = new List<RestoreExecutionStep>(manifest.Files.Count);
        long total = 0;

        foreach (var entry in manifest.Files)
        {
            total = checked(total + entry.Length);
            if (total > maximumTotalBytes)
                throw new InvalidOperationException("Backup restore exceeds the authorized aggregate byte ceiling.");

            var destination = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup restore destination escapes the restore root.");

            steps.Add(new RestoreExecutionStep(entry.RelativePath, destination, entry.Length, entry.Sha256));
        }

        if (steps.Select(static x => x.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != steps.Count)
            throw new InvalidOperationException("Backup restore contains colliding destination paths.");

        return new RestoreExecutionPlan(root, steps, total);
    }
}
