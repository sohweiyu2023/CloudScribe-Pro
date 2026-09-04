namespace CloudScribe.Architecture.Tests;

public sealed class FinalReleaseIntegrationContractTests
{
    [Fact]
    public void FinalBuildCannotRegressToStagedShellCopyOrDeadStage6To8Composition()
    {
        string repositoryRoot = FindRepositoryRoot();
        string shell = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs");
        string stage6Shell = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Stage6GoogleGeneration.cs");
        string stage7Shell = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Stage7VoiceLab.cs");
        string stage8Shell = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.Stage8RestoreRecovery.cs");
        string finalPresentation = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.FinalReleasePresentation.cs");
        string composition = ReadRepositoryFile(repositoryRoot, "src", "CloudScribe.App", "Composition", "CompositionRoot.cs");

        AssertNoStaleShellCopy(shell);
        AssertLiveStage6Boundary(stage6Shell);
        AssertLiveStage7And8Boundaries(stage7Shell, stage8Shell);
        AssertFinalPresentation(finalPresentation);
        AssertProductionComposition(composition);
    }

    private static void AssertNoStaleShellCopy(string shell)
    {
        string[] forbiddenShellCopy =
        [
            "Durable document creation arrives in Stage 3",
            "Retry and recovery actions become durable in Stage 3",
        ];

        foreach (string stale in forbiddenShellCopy)
        {
            Assert.False(shell.Contains(stale, StringComparison.OrdinalIgnoreCase),
                $"Stale staged lifecycle copy remains in the production shell: {stale}");
        }
    }

    private static void AssertLiveStage6Boundary(string stage6Shell)
    {
        string[] requiredLiveResolution =
        [
            "Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>>",
            "Func<long, bool, CancellationToken, Task>",
            "ApproveGoogleGenerationSpendAsync(",
            "await approve(authorizedMaximumMinorUnits, confirmedByUser, cancellationToken)",
            "var resolveContext = _resolveGoogleGenerationExecutionContext",
            "await resolveContext(cancellationToken)",
            "var coordinator = executionContext.Coordinator",
            "var state = executionContext.Snapshot",
        ];

        foreach (string required in requiredLiveResolution)
        {
            Assert.True(stage6Shell.Contains(required, StringComparison.Ordinal),
                $"Final Stage6 runtime, explicit approval and state must remain live and cancellation-aware: {required}");
        }
    }

    private static void AssertLiveStage7And8Boundaries(string stage7Shell, string stage8Shell)
    {
        string[] requiredStage7 =
        [
            "Func<CancellationToken, Task<VoiceLabCatalogUiState>>",
            "await capture(cancellationToken)",
            "Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabCatalogSelection>>",
            "await refresh(selected, cancellationToken)",
        ];
        foreach (string required in requiredStage7)
        {
            Assert.True(stage7Shell.Contains(required, StringComparison.Ordinal),
                $"Final Stage7 authorization/trust state must remain live, asynchronous and cancellation-aware: {required}");
        }

        string[] requiredStage8 =
        [
            "Func<CancellationToken, Task<RestoreRecoveryState>>",
            "await capture(cancellationToken)",
        ];
        foreach (string required in requiredStage8)
        {
            Assert.True(stage8Shell.Contains(required, StringComparison.Ordinal),
                $"Final Stage8 recovery state must remain live, asynchronous and cancellation-aware: {required}");
        }
    }

    private static void AssertFinalPresentation(string finalPresentation)
    {
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
            Assert.False(finalPresentation.Contains(stale, StringComparison.OrdinalIgnoreCase),
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
            Assert.True(finalPresentation.Contains(required, StringComparison.Ordinal),
                $"Final presentation repair is incomplete: {required}");
        }
    }

    private static void AssertProductionComposition(string composition)
    {
        string[] requiredProductionComposition =
        [
            "AddSingleton<GoogleGenerationProductionPendingApprovalStateOwner>();",
            "AddSingleton<GoogleGenerationProductionPendingApprovalPublisher>();",
            "AddSingleton<GoogleGenerationProductionSubmissionStateOwner>();",
            "AddSingleton<GoogleGenerationProductionSpendApprovalService>();",
            "new GoogleGenerationProductionRuntimeRequestSource(",
            "GetRequiredService<GoogleGenerationProductionSubmissionStateOwner>().ResolveCurrentAsync",
            "GetRequiredService<Stage6GoogleGenerationShellBinder>().Bind(",
            "GetRequiredService<GoogleGenerationProductionRuntimeRequestSource>().ResolveAsync",
            "ConfigureStage6GoogleGenerationSpendApproval",
            "approvalService.ApproveExplicitAsync(",
            "GetRequiredService<Stage7VoiceLabCatalogShellBinder>().Bind(viewModel);",
            "GetRequiredService<Stage7VoiceLabAuditionShellBinder>().Bind(viewModel);",
            "Stage8RestoreRecoveryShellBinder.ConfigurePersistedRecovery(",
            "viewModel.ApplyFinalReleasePresentation();",
        ];

        foreach (string required in requiredProductionComposition)
        {
            Assert.True(composition.Contains(required, StringComparison.Ordinal),
                $"Production composition is missing required Final wiring: {required}");
        }
    }

    private static string ReadRepositoryFile(string repositoryRoot, params string[] pathParts) =>
        File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));

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
