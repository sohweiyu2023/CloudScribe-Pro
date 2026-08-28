using System.Security;

namespace CloudScribe.Infrastructure.Configuration;

internal static class AppDataPathPolicy
{
    public static bool TryResolveOverride(
        string configuredPath,
        out string resolvedPath,
        out string failure)
    {
        resolvedPath = string.Empty;
        failure = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            failure = "AppDataDirectoryOverride must not be empty when it is supplied.";
            return false;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!Path.IsPathFullyQualified(expanded))
            {
                failure = "AppDataDirectoryOverride must resolve to a fully qualified local path.";
                return false;
            }

            if (IsNetworkOrDevicePath(expanded))
            {
                failure = "AppDataDirectoryOverride must resolve to a local filesystem path, not a network or device path.";
                return false;
            }

            string fullPath = Path.GetFullPath(expanded);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || PathsEqual(fullPath, Path.GetFullPath(root)))
            {
                failure = "AppDataDirectoryOverride cannot be a filesystem root.";
                return false;
            }

            resolvedPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or NotSupportedException
            or PathTooLongException
            or SecurityException)
        {
            failure = "AppDataDirectoryOverride is not a valid local application-data path.";
            return false;
        }
    }

    private static bool IsNetworkOrDevicePath(string path)
    {
        if (path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        return OperatingSystem.IsWindows()
            && (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || path.StartsWith("\\\\.\\", StringComparison.Ordinal));
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
