using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8DatabaseMigrationPlanTests
{
    [Fact]
    public void VerifiedContiguousPlanIsExecutable()
    {
        var first = DatabaseMigrationStep.Create(4, 5, "migrate-4-5", "ALTER TABLE jobs ADD COLUMN revision INTEGER;");
        var second = DatabaseMigrationStep.Create(5, 6, "migrate-5-6", "CREATE INDEX ix_jobs_revision ON jobs(revision);");
        var plan = new DatabaseMigrationPlan(new[] { first, second });
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [first.StableId] = first.ScriptSha256,
            [second.StableId] = second.ScriptSha256,
        };

        plan.ValidateExecutionPreconditions(4, backupVerified: true, transactionalExecutionAvailable: true, hashes);

        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void MissingBackupOrTamperedScriptBlocksMigration()
    {
        var step = DatabaseMigrationStep.Create(4, 5, "migrate-4-5", "ALTER TABLE jobs ADD COLUMN revision INTEGER;");
        var plan = new DatabaseMigrationPlan(new[] { step });
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal) { [step.StableId] = step.ScriptSha256 };

        Assert.Throws<InvalidOperationException>(() => plan.ValidateExecutionPreconditions(4, backupVerified: false, transactionalExecutionAvailable: true, hashes));
        Assert.Throws<InvalidOperationException>(() => plan.ValidateExecutionPreconditions(4, backupVerified: true, transactionalExecutionAvailable: true, new Dictionary<string, string> { [step.StableId] = new string('0', 64) }));
    }

    [Fact]
    public void NonContiguousOrBackwardPlansAreRejected()
    {
        var first = DatabaseMigrationStep.Create(4, 5, "migrate-4-5", "one");
        var disconnected = DatabaseMigrationStep.Create(6, 7, "migrate-6-7", "two");

        Assert.Throws<ArgumentException>(() => new DatabaseMigrationPlan(new[] { first, disconnected }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DatabaseMigrationStep.Create(5, 5, "bad", "noop"));
    }
}
