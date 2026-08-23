using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8RestoreTransactionJournalTests
{
    [Fact]
    public void RestoreCannotVerifyUntilEveryBoundFileWasCopied()
    {
        var plan = Plan("a.db", "b.db");
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var journal = RestoreTransactionJournal.Start(plan, now).BeginCopy(plan, now.AddSeconds(1));
        journal = journal.MarkCopied(plan, "a.db", now.AddSeconds(2));

        Assert.Throws<InvalidOperationException>(() => journal.BeginVerification(plan, now.AddSeconds(3)));

        journal = journal.MarkCopied(plan, "b.db", now.AddSeconds(3));
        journal = journal.BeginVerification(plan, now.AddSeconds(4));
        journal = journal.Commit(plan, now.AddSeconds(5));

        Assert.Equal(RestoreTransactionState.Committed, journal.State);
    }

    [Fact]
    public void RestartedJournalRejectsPlanSwapOrBackwardStateMovement()
    {
        var plan = Plan("a.db");
        var changed = Plan("a.db", "extra.db");
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var journal = RestoreTransactionJournal.Start(plan, now).BeginCopy(plan, now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => journal.MarkCopied(changed, "a.db", now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() => journal.MarkCopied(plan, "a.db", now.AddSeconds(-1)));
    }

    [Fact]
    public void FailureBeforeCommitRequiresExplicitRollbackState()
    {
        var plan = Plan("a.db");
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var journal = RestoreTransactionJournal.Start(plan, now)
            .BeginCopy(plan, now.AddSeconds(1))
            .RequireRollback(plan, now.AddSeconds(2));

        Assert.Equal(RestoreTransactionState.RollbackRequired, journal.State);
        Assert.Throws<InvalidOperationException>(() => journal.BeginVerification(plan, now.AddSeconds(3)));
    }

    private static RestoreExecutionPlan Plan(params string[] names)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-restore-root"));
        var steps = names.Select((name, index) => new RestoreExecutionStep(
            name,
            Path.Combine(root, name),
            10 + index,
            new string((char)('a' + index), 64))).ToArray();
        return new RestoreExecutionPlan(root, steps, steps.Sum(static x => x.Length));
    }
}
