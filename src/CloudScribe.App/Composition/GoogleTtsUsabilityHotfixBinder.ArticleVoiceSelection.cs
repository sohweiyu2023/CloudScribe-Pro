using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static async Task<bool> ConfirmArticleVoiceSelectionAsync(Window owner, ShellViewModel model, ChirpWholeArticleReview review)
    {
        var selection = GoogleVoiceComparisonSelection.TryCreate(model.SelectedVoiceLabVoice, model.VoiceLabLocaleFilter);
        if (selection is null || selection.MatchesReviewedVoice(review.VoiceName)) return true;
        var dialog = CreateArticleExportDialog(); dialog.Title = "Choose a separate document for this voice";
        dialog.Width = 640; dialog.Height = 340;
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        var message = NewDescription($"You selected {selection.Voice.VoiceStableId}, but this saved document is already tied to {review.VoiceName}. "
            + "To compare the same text in another voice, copy the text, open Library > New document, paste it, give it a different title, and press Ctrl+S. Then choose the other voice and review that copy. "
            + "Each copy needs its own exact cost approval. Existing audio, spending limits and requests stay unchanged. No paid request has started.");
        AutomationProperties.SetName(message, "Selected voice differs from this document's approved voice");
        var close = NewButton("Close and return to Studio"); close.Click += (_, _) => dialog.Close();
        body.Children.Add(message); body.Children.Add(close); dialog.Content = new ScrollViewer { Content = body };
        DialogBounds.Fit(dialog, owner);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return false;
    }
}
