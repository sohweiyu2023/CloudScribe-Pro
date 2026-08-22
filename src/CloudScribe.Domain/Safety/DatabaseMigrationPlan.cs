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

public sealed class DatabaseMigrationPlan
{
    private readonly IReadOnlyList<DatabaseMigrationStep> _steps;

    public DatabaseMigrationPlan(IEnumerable<DatabaseMigrationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToArray();
        if (_steps.Count == 0) throw new ArgumentException("At least one migration step is required.", nameof(steps));
        if (_steps.Select(static step => step.StableId).Distinct(StringComparer.Ordinal).Count() != _steps.Count)
        {
            throw new ArgumentException("Migration stable ids must be unique.", nameof(steps));
        }

        for (var index = 1; index < _steps.Count; index++)
        {
            if (_steps[index - 1].ToVersion != _steps[index].FromVersion)
            {
                throw new ArgumentException("Migration plan versions must be contiguous and ordered.", nameof(steps));
            }
        }
    }

    public IReadOnlyList<DatabaseMigrationStep> Steps => _steps;

    public void ValidateExecutionPreconditions(
        int currentVersion,
        bool backupVerified,
        bool transactionalExecutionAvailable,
        IReadOnlyDictionary<string, string> availableScriptHashes)
    {
        ArgumentNullException.ThrowIfNull(availableScriptHashes);
        if (currentVersion != _steps[0].FromVersion)
        {
            throw new InvalidOperationException("Database version does not match the migration plan starting version.");
        }
        if (_steps.Any(static step => step.RequiresBackup) && !backupVerified)
        {
            throw new InvalidOperationException("A verified backup is required before this migration plan can run.");
        }
        if (_steps.Any(static step => step.Transactional) && !transactionalExecutionAvailable)
        {
            throw new InvalidOperationException("Transactional migration execution is required but unavailable.");
        }

        foreach (var step in _steps)
        {
            if (!availableScriptHashes.TryGetValue(step.StableId, out var actualHash) ||
                !string.Equals(actualHash, step.ScriptSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Migration script provenance mismatch for '{step.StableId}'.");
            }
        }
    }
}
