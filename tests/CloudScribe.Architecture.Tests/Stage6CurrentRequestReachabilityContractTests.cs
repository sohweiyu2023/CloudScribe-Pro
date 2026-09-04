namespace CloudScribe.Architecture.Tests;

public sealed class Stage6CurrentRequestReachabilityContractTests
{
    [Fact]
    public void ProductionShellPublishesOneCoherentCurrentRequestBeforePreparation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "ViewModels",
            "ShellViewModel.Stage6GoogleGeneration.cs"));
        string composition = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "Composition",
            "CompositionRoot.cs"));

        string[] requiredShellBoundary =
        [
            "Action<GoogleGenerationProductionCompileEvidence>",
            "ConfigureStage6GoogleGenerationCurrentRequestPublication(",
            "publish(currentRequest);",
            "await ApproveGoogleGenerationSpendAsync(",
        ];
        foreach (string required in requiredShellBoundary)
        {
            Assert.Contains(required, shell, StringComparison.Ordinal);
        }

        string[] requiredProductionWiring =
        [
            "GetRequiredService<GoogleGenerationProductionCurrentRequestStateOwner>()",
            "ConfigureStage6GoogleGenerationCurrentRequestPublication(evidence =>",
            "currentRequestOwner.Publish(evidence)",
            "preparationCoordinator.PrepareCurrentAsync(cancellationToken)",
        ];
        foreach (string required in requiredProductionWiring)
        {
            Assert.Contains(required, composition, StringComparison.Ordinal);
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
