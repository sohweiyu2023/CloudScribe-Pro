using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Safety;

public sealed record DatabaseMigrationStep(
    int FromVersion,
    int ToVersion,
    string StableId,
    string ScriptSha256,
    bool RequiresBackup,
    bool Transactional)
{
    public static DatabaseMigrationStep Create(
        int fromVersion,
        int toVersion,
        string stableId,
        string migrationScript,
        bool requiresBackup = true,
        bool transactional = true)
    {
        if (fromVersion < 0) throw new ArgumentOutOfRangeException(nameof(fromVersion));
        if (toVersion <= fromVersion) throw new ArgumentOutOfRangeException(nameof(toVersion), "Migrations must move strictly forward.");
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationScript);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migrationScript))).ToLowerInvariant();
        return new DatabaseMigrationStep(fromVersion, toVersion, stableId, hash, requiresBackup, transactional);
    }
}
