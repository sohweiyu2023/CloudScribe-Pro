namespace CloudScribe.Architecture.Tests;

public sealed class Stage6CurrentRequestReachabilityContractTests
{
    [Fact]
    public void ProductionShellDoesNotAcceptCallerSuppliedAuthorizationEvidence()
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

        string[] forbiddenCallerSuppliedBoundary =
        [
            "Action<GoogleGenerationProductionCompileEvidence>",
            "ConfigureStage6GoogleGenerationCurrentRequestPublication(",
            "GoogleGenerationProductionCompileEvidence currentRequest",
            "publish(currentRequest);",
        ];
        foreach (string forbidden in forbiddenCallerSuppliedBoundary)
        {
            Assert.DoesNotContain(forbidden, shell, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, composition, StringComparison.Ordinal);
        }

        string[] requiredProductionWiring =
        [
            "services.AddSingleton<GoogleGenerationProductionCurrentRequestStateOwner>();",
            "preparationCoordinator.PrepareCurrentAsync(cancellationToken)",
            "approvalService.ApproveExplicitAsync(",
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
