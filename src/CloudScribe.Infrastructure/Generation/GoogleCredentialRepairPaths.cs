namespace CloudScribe.Infrastructure.Generation;

internal static class GoogleCredentialRepairPaths
{
    internal static void RequirePhysical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Repair requires an absolute local path.");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked credential repair paths require separate review.");
    }
}
