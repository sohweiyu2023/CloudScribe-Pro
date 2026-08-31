using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class Stage8RestoreRecoveryShellBinderTests
{
    [Theory]
    [InlineData("recovery/journal.json", 0)]
    [InlineData("staging", 1)]
    [InlineData("backups", 2)]
    public void NormalizeRecoveryPaths_RejectsRelativeInputs(string relativePath, int relativePathIndex)
    {
        string root = Path.Combine(Path.GetTempPath(), "CloudScribe", "stage8-recovery-test");
        string journalPath = Path.Combine(root, "recovery", "journal.json");
        string stagingRoot = Path.Combine(root, "staging");
        string backupRoot = Path.Combine(root, "backups");

        if (relativePathIndex == 0)
        {
            journalPath = relativePath;
        }
        else if (relativePathIndex == 1)
        {
            stagingRoot = relativePath;
        }
        else
        {
            backupRoot = relativePath;
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Stage8RestoreRecoveryShellBinder.NormalizeRecoveryPaths(journalPath, stagingRoot, backupRoot));

        Assert.Equal(
            "Stage 8 restore recovery paths must be explicitly fully qualified.",
            exception.Message);
    }
}
