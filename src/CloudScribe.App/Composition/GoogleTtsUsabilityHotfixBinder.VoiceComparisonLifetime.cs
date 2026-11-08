using Avalonia.Controls;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool _voiceComparisonOpen;

    private async Task OpenVoiceComparisonAsync(MainWindow owner, ShellViewModel model)
    {
        if (_voiceComparisonOpen) return;
        _voiceComparisonOpen = true;
        try { await ShowVoiceComparisonAsync(owner, model).ConfigureAwait(true); }
        finally { _voiceComparisonOpen = false; }
    }

    private static void RestoreDialogInvoker(Window owner, Control? invoker)
    {
        if (owner.IsVisible && owner.IsEnabled && invoker is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
            && ReferenceEquals(TopLevel.GetTopLevel(invoker), owner)
            && !ReferenceEquals(owner.FocusManager?.GetFocusedElement(), invoker))
            invoker.Focus();
    }

    private static async Task CancelCatalogCallbacksAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(true); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Cancellation has already been requested. A broken callback cannot trap the
            // dialog, reclaim its diagnostic lease or override a newer selection/status.
            // The original operation remains responsible for its eventual completion.
        }
    }
}
