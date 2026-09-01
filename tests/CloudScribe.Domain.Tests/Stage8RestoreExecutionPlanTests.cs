using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8RestoreExecutionPlanTests
{
    [Fact]
    public void CreatesAbsoluteBoundedRestoreSteps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cloudscribe-restore-plan-{Guid.NewGuid():N}");
        var manifest = Manifest(
            new BackupFileEntry("db/main.sqlite", 10, new string('a', 64)),
            new BackupFileEntry("state/settings.json", 20, new string('b', 64)));

        var plan = RestoreExecutionPlan.Create(root, manifest, 100, 10);

        Assert.Equal(Path.GetFullPath(root), plan.RestoreRoot);
        Assert.Equal(30, plan.TotalBytes);
        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.True(Path.IsPathFullyQualified(step.DestinationPath)));
    }

    [Fact]
    public void RelativeRestoreRootFailsClosedInsteadOfUsingAmbientWorkingDirectory()
    {
        var manifest = Manifest(new BackupFileEntry("db/main.sqlite", 10, new string('a', 64)));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => RestoreExecutionPlan.Create("relative-restore-root", manifest, 100, 10));

        Assert.Contains("explicitly fully qualified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateByteAndFileCeilingsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cloudscribe-restore-plan-{Guid.NewGuid():N}");
        var manifest = Manifest(
            new BackupFileEntry("a.bin", 60, new string('a', 64)),
            new BackupFileEntry("b.bin", 60, new string('b', 64)));

        Assert.Throws<InvalidOperationException>(() => RestoreExecutionPlan.Create(root, manifest, 100, 10));
        Assert.Throws<InvalidOperationException>(() => RestoreExecutionPlan.Create(root, manifest, 1000, 1));
    }

    private static BackupRestoreManifest Manifest(params BackupFileEntry[] files) =>
        new(1, DateTimeOffset.UtcNow, files);
}
