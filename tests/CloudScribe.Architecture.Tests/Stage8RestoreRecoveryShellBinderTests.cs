using CloudScribe.App.Composition;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Architecture.Tests;

public sealed class Stage8RestoreRecoveryShellBinderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Configure_RejectsRelativeRecoveryPathsBeforeUsingDependencies(int relativePathIndex)
    {
        string root = Path.Combine(Path.GetTempPath(), "CloudScribe", "stage8-recovery-test");
        string journalPath = Path.Combine(root, "recovery", "journal.json");
        string stagingRoot = Path.Combine(root, "staging");
        string backupRoot = Path.Combine(root, "backups");

        switch (relativePathIndex)
        {
            case 0:
                journalPath = Path.Combine("recovery", "journal.json");
                break;
            case 1:
                stagingRoot = "staging";
                break;
            case 2:
                backupRoot = "backups";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(relativePathIndex));
        }

        Func<CancellationToken, Task<RestoreExecutionPlan>> captureCurrentPlanAsync =
            _ => throw new InvalidOperationException("The plan provider must not be invoked while validating paths.");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Stage8RestoreRecoveryShellBinder.Configure(
                null!,
                null!,
                null!,
                journalPath,
                stagingRoot,
                backupRoot,
                null!,
                captureCurrentPlanAsync));

        Assert.Equal(
            "Stage 8 restore recovery paths must be explicitly fully qualified.",
            exception.Message);
    }
}
