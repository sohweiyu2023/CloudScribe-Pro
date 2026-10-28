using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Action<GenerationProviderRequest, long>? _attachPrivateGoogleDiagnostic;
    private Action? _invalidatePrivateGoogleDiagnostic;
    public long PrivateDiagnosticRequestVersion => Volatile.Read(ref _googleRequestVersion);

    public void ConfigurePrivateGoogleDiagnostics(Action<GenerationProviderRequest, long> attach, Action invalidate)
    {
        _attachPrivateGoogleDiagnostic = attach ?? throw new ArgumentNullException(nameof(attach));
        _invalidatePrivateGoogleDiagnostic = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
    }

    private void AttachPrivateGoogleDiagnostic(GenerationProviderRequest request, long version)
    {
        try { _attachPrivateGoogleDiagnostic?.Invoke(request, version); }
        catch (Exception error) when (!IsFatalUiException(error)) { InvalidatePrivateGoogleDiagnostic(); }
    }

    private void InvalidatePrivateGoogleDiagnostic()
    {
        try { _invalidatePrivateGoogleDiagnostic?.Invoke(); }
        catch (Exception error) when (!IsFatalUiException(error)) { /* Diagnostics never change generation/financial behavior. */ }
    }
}
