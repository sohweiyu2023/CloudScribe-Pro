using CloudScribe.Application.Safety;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed record RestoreRecoveryContext(
    RestoreRecoveryState State,
    RestoreTransactionJournal Journal);

public sealed class RestoreRecoveryStateResolver
{
    private readonly FileAuthenticatedRestoreRecoveryJournalStore _journalStore;
    private readonly string _stagingRoot;

    public RestoreRecoveryStateResolver(
        FileAuthenticatedRestoreRecoveryJournalStore journalStore,
        string stagingRoot)
    {
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        if (!Path.IsPathFullyQualified(_stagingRoot))
            throw new InvalidOperationException("Restore recovery staging root must resolve to an absolute path.");
    }

    public async Task<RestoreRecoveryContext?> ResolveAsync(
        RestoreExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        RestoreTransactionJournal? journal = await _journalStore
            .LoadAuthenticatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (journal is null)
            return null;

        journal.EnsurePlan(plan);
        RequireTrustedRoots(_stagingRoot, plan.RestoreRoot);

        if (journal.State == RestoreTransactionState.Committed)
            throw new InvalidOperationException("Committed restore transactions are terminal and cannot enter interrupted recovery.");

        var state = new RestoreRecoveryState(
            JournalAuthenticated: true,
            PlanIdentityMatches: true,
            StagingRootTrusted: true,
            DestinationRootTrusted: true,
            RollbackRequired: journal.State == RestoreTransactionState.RollbackRequired,
            AlreadyRolledBack: journal.State == RestoreTransactionState.RolledBack);

        return new RestoreRecoveryContext(state, journal);
    }

    private static void RequireTrustedRoots(string stagingRoot, string restoreRoot)
    {
        string staging = Path.GetFullPath(stagingRoot);
        string destination = Path.GetFullPath(restoreRoot);
        if (!Path.IsPathFullyQualified(staging) || !Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Restore recovery filesystem roots must be absolute paths.");
        if (!Directory.Exists(staging))
            throw new InvalidOperationException("Restore recovery staging root does not exist.");

        RequirePhysicalDirectoryChain(staging, "staging");
        RequirePhysicalDirectoryChain(Directory.Exists(destination)
            ? destination
            : Path.GetDirectoryName(destination) ?? destination, "destination");

        if (IsSameOrNested(staging, destination) || IsSameOrNested(destination, staging))
            throw new InvalidOperationException("Restore recovery staging and destination roots must be physically separate trees.");
    }

    private static bool IsSameOrNested(string candidate, string root)
    {
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequirePhysicalDirectoryChain(string directory, string role)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Restore recovery {role} root may not traverse a reparse-point directory: {current.FullName}");
            current = current.Parent;
        }
    }
}
