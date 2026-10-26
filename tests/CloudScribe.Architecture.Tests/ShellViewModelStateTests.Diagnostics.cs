using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task DiagnosticExportFailureKeepsUiUsableAndAllowsExplicitRetry()
    {
        using ShellViewModel model = CreateViewModel();
        model.DocumentText = "Existing text stays unchanged.";
        int attempts = 0;
        model.ConfigureStage5GenerationDiagnostics(
            new GenerationSupportBundleExportCoordinator(new GenerationSupportBundleService(), (_, _) =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(new IOException("Synthetic disk failure")) : Task.CompletedTask;
            }),
            currentPolicyAllowsDiagnostics: true);

        await model.ExportGenerationDiagnosticsCommand.ExecuteAsync(null);

        Assert.Contains("Metadata export failed", model.StatusMessage, StringComparison.Ordinal);
        Assert.True(model.CanExportGenerationDiagnostics);
        Assert.Equal("Existing text stays unchanged.", model.DocumentText);

        await model.ExportGenerationDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal(2, attempts);
        Assert.Contains("saved locally", model.StatusMessage, StringComparison.Ordinal);
        Assert.True(model.CanExportGenerationDiagnostics);
    }
}
