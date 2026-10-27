using Avalonia;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool _recoveryDialogOpen;

    private void AddRecoveryReviewControl(MainWindow window, StackPanel host, ShellViewModel model)
    {
        var button = NewButton("Review an additional attempt with earlier holds retained.");
        button.Click += async (_, _) => await OpenRecoveryReviewAsync(window, model).ConfigureAwait(true);
        host.Children.Add(button);
    }

    private async Task OpenRecoveryReviewAsync(MainWindow owner, ShellViewModel model)
    {
        if (_recoveryDialogOpen) return;
        _recoveryDialogOpen = true;
        try
        {
            var keys = await recoveryPreparation.ListCurrentHistoricalKeysAsync(model.CaptureGoogleTtsStudioRequestSelection(), CancellationToken.None).ConfigureAwait(true);
            var dialog = CreateArticleExportDialog(); dialog.Title = "Review one additional narration attempt";
            using var lifetime = new CancellationTokenSource();
            dialog.Closed += (_, _) => lifetime.Cancel();
            var controls = CreateRecoveryControls(keys);
            var state = new RecoveryDialogState();
            BindRecoveryControls(dialog, model, controls, state, keys, lifetime.Token);
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (state.Active is not null) await state.Active.ConfigureAwait(true);
            if (state.Preview is { } preview) recoveryPreparation.Discard(preview.PreviewId);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "Recovery review is unavailable. Earlier attempts, audio and financial holds were preserved."; }
        finally { _recoveryDialogOpen = false; }
    }

    private static RecoveryControls CreateRecoveryControls(IReadOnlyList<string> keys) => new(
        new ComboBox { ItemsSource = keys.Select((_, i) => $"Historical attempt {i + 1}").ToArray(), IsEnabled = keys.Count > 0 },
        new NumericUpDown { Minimum = 0, Maximum = long.MaxValue, Increment = 1, Value = 0, FormatString = "0" },
        NewButton("Prepare exact recovery review"), new CheckBox { Content = "Earlier uncertain attempts may still be charged. Retain all their holds.", IsEnabled = false },
        NewButton("Reserve one additional attempt; do not generate"), NewDescription(keys.Count == 0
            ? "No independently admitted historical narration matches the selected saved document and current account/project. No recovery is available."
            : "This review never sends audio requests. Enter the additional maximum in smallest currency units (for USD, cents). Independently reviewed project and allocation limits still apply."));

    private void BindRecoveryControls(Window dialog, ShellViewModel model, RecoveryControls c, RecoveryDialogState state,
        IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        c.Approve.IsEnabled = false;
        void Invalidate()
        {
            if (state.Preview is { } old) recoveryPreparation.Discard(old.PreviewId);
            state.Preview = null; c.Acknowledge.IsChecked = false; c.Acknowledge.IsEnabled = false; c.Approve.IsEnabled = false;
        }
        c.Choice.SelectionChanged += (_, _) => Invalidate();
        c.Maximum.ValueChanged += (_, _) => Invalidate();
        c.Acknowledge.IsCheckedChanged += (_, _) => c.Approve.IsEnabled = state.Preview is not null && c.Acknowledge.IsChecked == true && state.Active is not { IsCompleted: false };
        c.Prepare.Click += (_, _) => state.Active = RunRecoveryDialogActionAsync(c, state, async () =>
        {
            Invalidate();
            if (c.Choice.SelectedIndex < 0 || c.Maximum.Value is not { } maximum || decimal.Truncate(maximum) != maximum)
                throw new InvalidOperationException("Choose a historical attempt and an exact maximum.");
            var p = await model.PrepareGoogleRecoveryAsync(keys[c.Choice.SelectedIndex], checked((long)maximum), cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested) { recoveryPreparation.Discard(p.PreviewId); return; }
            state.Preview = p; c.Acknowledge.IsEnabled = true;
            c.Approve.Content = p.ExistingApproval is null ? "Reserve one additional attempt; do not generate" : "Activate the same reserved attempt; do not generate";
            c.Status.Text = RecoveryReviewText(p);
        }, cancellationToken);
        c.Approve.Click += (_, _) => state.Active = RunRecoveryDialogActionAsync(c, state, async () =>
        {
            var p = state.Preview ?? throw new InvalidOperationException("Prepare a current review.");
            if (c.Acknowledge.IsChecked != true) throw new InvalidOperationException("Acknowledge retained historical holds.");
            await model.ApproveGoogleRecoveryAsync(new(p.PreviewId, p.ReviewSha256, p.AdditionalMaximumMinorUnits, true, true), cancellationToken).ConfigureAwait(true);
            state.Approved = true; c.Status.Text = model.StatusMessage;
            c.Acknowledge.IsEnabled = false; c.Approve.IsEnabled = false;
        }, cancellationToken);
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        foreach (Control control in new Control[] { c.Status, c.Choice, NewDescription("Additional maximum (smallest currency units)"), c.Maximum, c.Prepare, c.Acknowledge, c.Approve, CreateHistoricalCloseButton(dialog) }) body.Children.Add(control);
        dialog.Content = new ScrollViewer { Content = body };
    }

    private static string RecoveryReviewText(GoogleRecoveryPreview p)
    {
        string reservation = p.ExistingApproval is null ? $"Additional maximum {p.AdditionalMaximumMinorUnits}."
            : $"Resume the SAME existing reserved attempt ({p.AdditionalMaximumMinorUnits}); additional reservation now ZERO.";
        return $"{p.Intent.Currency}, scale {p.Intent.Scale}: current estimate {p.Intent.EstimateMinorUnits}. {reservation} Retained commitments {p.Budget.RetainedCommittedMinorUnits}; project ceiling {p.Budget.ProjectCeilingMinorUnits}; remaining allocation {p.Budget.RemainingAllocationMinorUnits}. Voice {p.Intent.Envelope.VoiceName}. Earlier holds stay reserved. Confirmation does not generate audio.";
    }

    private static async Task RunRecoveryDialogActionAsync(RecoveryControls c, RecoveryDialogState state, Func<Task> action, CancellationToken cancellationToken)
    {
        if (state.Active is { IsCompleted: false } || state.Approved) return;
        c.Choice.IsEnabled = false; c.Maximum.IsEnabled = false; c.Prepare.IsEnabled = false; c.Approve.IsEnabled = false;
        try { await action().ConfigureAwait(true); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { c.Status.Text = "Recovery stopped safely. Evidence, selection, price or financial state needs a fresh review. Do not retry synthesis or clear earlier holds."; }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !state.Approved)
            { c.Choice.IsEnabled = true; c.Maximum.IsEnabled = true; c.Prepare.IsEnabled = true; }
        }
    }

    private sealed record RecoveryControls(ComboBox Choice, NumericUpDown Maximum, Button Prepare, CheckBox Acknowledge, Button Approve, TextBlock Status);
    private sealed class RecoveryDialogState { public GoogleRecoveryPreview? Preview { get; set; } public Task? Active { get; set; } public bool Approved { get; set; } }
}
