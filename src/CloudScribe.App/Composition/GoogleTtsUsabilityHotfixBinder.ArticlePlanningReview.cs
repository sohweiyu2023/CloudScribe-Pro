using Avalonia.Automation;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task OpenReadOnlyArticleReviewAsync(Window owner, ShellViewModel model, Guid document, Exception admissionError, CancellationToken token)
    {
        // Historical coverage may be inspected without renewing expired paid authority or consuming an approval.
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        decimal planningRate = GoogleVoiceComparisonInformation.Rate(_comparisonPricing, selection.VoiceStableId)?.UsdPerMillion
            ?? throw new InvalidOperationException("No verified planning character price is available for this selected voice.");
        var inspection = await chirpArticles!.InspectStoredArticleAsync(document, selection.VoiceStableId,
            selection.LanguageCode, planningRate, token).ConfigureAwait(true);
        var dialog = CreateArticleExportDialog(); dialog.Title = "Full article cost and saved audio"; dialog.Width = 720; dialog.Height = 580;
        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 14 };
        var description = NewDescription($"Article: {model.DocumentTitle}. Voice: {inspection.VoiceName}. Original article: {inspection.SourceScalars} characters. "
            + $"Saved audio: {inspection.SavedParts}/{inspection.TotalParts} parts verified and reused. "
            + $"Remaining: {inspection.RemainingRequests} internal requests, {inspection.RemainingScalars} characters. "
            + $"Planning list estimate: US${inspection.PlanningListPriceUsd:F4} before tax at US${inspection.PlanningUsdPerMillionCharacters:F2}/million characters; "
            + $"rounding each request upward gives US${inspection.PlanningRoundedCentHolds / 100m:F2} in planning reservations. "
            + "This planning rate is not a fresh pricing/spending approval. GST, conversion and all historical commitments must remain included in your total cap. This review has not reserved money or sent a request; all historical holds remain.");
        AutomationProperties.SetName(description, "Read-only full article cost and saved audio");
        string recovery = inspection.RecoveryRequiredOrdinals.Count == 0 ? "No existing failed or reserved request is present in the remaining coverage."
            : $"Existing request(s) {string.Join(", ", inspection.RecoveryRequiredOrdinals.Take(20))} need explicit reconciliation or recovery. They will not be silently resent.";
        var reason = NewDescription("Generation is not ready. A fresh whole-article authorization and current exact cost review are required. "
            + recovery + " " + ReportLocalFailure(LocalRequestStage.ArticleOpen, admissionError));
        AutomationProperties.SetName(reason, "Why complete article generation is unavailable");
        var generate = NewButton("Generate complete article MP3"); generate.IsEnabled = false;
        var close = NewButton("Close review"); close.Click += (_, _) => dialog.Close();
        foreach (Control control in new Control[] { description, reason, generate, close }) body.Children.Add(control);
        Guid? completed = await chirpArticles.FindCompletedWholeArticleAsync(document, token).ConfigureAwait(true);
        if (completed is not null) AddCompletedArticleOutputControls(dialog, body, () => completed, reason);
        dialog.Content = new ScrollViewer { Content = body };
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}
