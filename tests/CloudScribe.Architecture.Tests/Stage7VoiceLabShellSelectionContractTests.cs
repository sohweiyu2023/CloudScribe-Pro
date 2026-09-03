namespace CloudScribe.Architecture.Tests;

public sealed class Stage7VoiceLabShellSelectionContractTests
{
    [Fact]
    public void TrustedCatalogAutoSelectionRequiresExactlyOneVoice()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "ViewModels",
            "ShellViewModel.Stage7VoiceLab.cs"));

        Assert.Contains(
            "if (SelectedVoiceLabVoice is null && VoiceLabCatalogResults.Count == 1)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedVoiceLabVoice = VoiceLabCatalogResults[0];",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedVoiceLabVoice is null && VoiceLabCatalogResults.Count > 0",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))
                && File.Exists(Path.Combine(directory.FullName, "SESSION_STATE.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CloudScribe repository root from the test working directory.");
    }
}
