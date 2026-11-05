using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task RequestCaptureWithoutVerifiedVoicePreservesPreciseLocalGuard()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave);
        await model.NewDocumentCommand.ExecuteAsync(null);
        model.DocumentText = "Synthetic local text; never sent.";
        model.SelectedVoiceLabVoice = null;
        var error = Assert.Throws<InvalidOperationException>(() => model.CaptureGoogleTtsStudioRequestSelection());
        Assert.Equal(LocalRequestReason.VerifiedVoiceMissing, LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason);
        Assert.False(model.CanGenerateWithGoogle);
    }
}
