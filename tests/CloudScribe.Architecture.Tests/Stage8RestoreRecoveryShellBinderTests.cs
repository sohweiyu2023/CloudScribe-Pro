using System.Runtime.CompilerServices;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class Stage8RestoreRecoveryShellBinderTests
{
    [Theory]
    [InlineData("recovery/journal.json", "/staging", "/backups")]
    [InlineData("/recovery/journal.json", "staging", "/backups")]
    [InlineData("/recovery/journal.json", "/staging", "backups")]
    public void Configure_RejectsRelativeRecoveryPathsBeforeUsingDependencies(
        string journalPath,
        string stagingRoot,
        string backupRoot)
    {
        ShellViewModel viewModel = Uninitialized<ShellViewModel>();
        RestoreRecoveryExecutionCompositionFactory compositionFactory = Uninitialized<RestoreRecoveryExecutionCompositionFactory>();
        CredentialReference authenticationKeyReference = Uninitialized<CredentialReference>();
        AtomicVerifiedRestoreExecutor restoreExecutor = Uninitialized<AtomicVerifiedRestoreExecutor>();
        Func<CancellationToken, Task<RestoreExecutionPlan>> captureCurrentPlanAsync =
            _ => throw new InvalidOperationException("The plan provider must not be invoked while validating paths.");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Stage8RestoreRecoveryShellBinder.Configure(
                viewModel,
                compositionFactory,
                authenticationKeyReference,
                journalPath,
                stagingRoot,
                backupRoot,
                restoreExecutor,
                captureCurrentPlanAsync));

        Assert.Equal(
            "Stage 8 restore recovery paths must be explicitly fully qualified.",
            exception.Message);
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
