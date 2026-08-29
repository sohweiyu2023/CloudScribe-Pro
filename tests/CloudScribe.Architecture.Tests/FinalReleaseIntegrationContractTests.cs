namespace CloudScribe.Architecture.Tests;

public sealed class FinalReleaseIntegrationContractTests
{
    [Fact]
    public void FinalBuildCannotRegressToStagedShellCopyOrDeadStage6To8Composition()
    {
        string repositoryRoot = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "ViewModels",
            "ShellViewModel.cs"));
        string finalPresentation = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "ViewModels",
            "ShellViewModel.FinalReleasePresentation.cs"));
        string composition = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "Composition",
            "CompositionRoot.cs"));

        string[] forbiddenShellCopy =
        [
            "Durable document creation arrives in Stage 3",
            "Retry and recovery actions become durable in Stage 3",
        ];
        foreach (string stale in forbiddenShellCopy)
        {
            Assert.False(
                shell.Contains(stale, StringComparison.OrdinalIgnoreCase),
                $"Stale staged lifecycle copy remains in the production shell: {stale}");
        }

        string[] forbiddenFinalCopy =
        [
            "arrive in Stage 3",
            "arrives in Stage 3",
            "introduced in Stage 5",
            "generation engine incomplete",
            "exact v2.22",
            "schema 1.1.5/seed",
            "STAGE 2 PREVIEW",
            "UNSAVED PREVIEW",
        ];
        foreach (string stale in forbiddenFinalCopy)
        {
            Assert.False(
                finalPresentation.Contains(stale, StringComparison.OrdinalIgnoreCase),
                $"Stale staged copy remains in active Final presentation: {stale}");
        }

        string[] requiredFinalPresentation =
        [
            "DocumentTitle = \"CloudScribe Pro Local Workspace\";",
            "Provider-backed generation is available only through the production safety gates.",
            "OutlineEntries.Clear();",
        ];
        foreach (string required in requiredFinalPresentation)
        {
            Assert.True(
                finalPresentation.Contains(required, StringComparison.Ordinal),
                $"Final presentation repair is incomplete: {required}");
        }

        string[] requiredProductionComposition =
        [
            "viewModel.ConfigureStage6GoogleGeneration(",
            "viewModel.ConfigureStage7VoiceLabCatalog(",
            "viewModel.ConfigureStage7VoiceLabAudition(",
            "viewModel.ConfigureStage8RestoreRecovery(",
            "viewModel.ApplyFinalReleasePresentation();",
        ];
        foreach (string required in requiredProductionComposition)
        {
            Assert.True(
                composition.Contains(required, StringComparison.Ordinal),
                $"Production composition is missing required Final wiring: {required}");
        }
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
