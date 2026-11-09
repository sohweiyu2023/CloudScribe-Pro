using Avalonia.Controls;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private void MountFirstRunInvitation(MainWindow owner, ShellViewModel model)
    {
        if (owner.FindControl<Border>("FirstRunWelcomeCard") is not { } card) return;
        // The invitation does not move focus, open a dialog or touch any provider.
        card.IsVisible = preferences.IsFreshProfile && !preferences.State.QuickStartSeen;
        if (owner.FindControl<Button>("StartTutorialButton") is { } start)
            start.Click += async (_, _) => await OpenQuickStartObservedAsync(owner, model).ConfigureAwait(true);
        if (owner.FindControl<Button>("DismissTutorialButton") is { } dismiss)
            dismiss.Click += (_, _) =>
            {
                DismissFirstRunInvitation(owner, model);
                owner.FindControl<Button>("QuickStartHelpButton")?.Focus();
            };
    }

    private void DismissFirstRunInvitation(MainWindow owner, ShellViewModel model)
    {
        if (owner.FindControl<Border>("FirstRunWelcomeCard") is { IsVisible: true } card)
        {
            card.IsVisible = false;
            owner.FindControl<Button>("QuickStartHelpButton")?.Focus();
        }
        if (preferences.State.QuickStartSeen) return;
        try { preferences.RememberQuickStartSeen(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { model.StatusMessage = "The tutorial invitation is closed, but that preference could not be saved. Your existing settings are preserved. Help can reopen the tutorial."; }
    }
}
