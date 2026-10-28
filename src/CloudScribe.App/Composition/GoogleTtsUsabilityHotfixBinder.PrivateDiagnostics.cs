using System.Security.Cryptography;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private PrivateErrorCaptureBinding? _privateCaptureUiBinding;
    private bool _privateDialogOpen;

    private void ConfigurePrivateDiagnostics(MainWindow window, ShellViewModel model)
    {
        void Invalidate() { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); }
        window.Closed += (_, _) => Invalidate();
        model.ConfigurePrivateGoogleDiagnostics((request, version) =>
        {
            var prior = _privateCaptureUiBinding; _privateCaptureUiBinding = null;
            if (prior is null) return;
            var current = model.CaptureGoogleTtsStudioRequestSelection();
            privateDiagnostics.Attach(request, prior with
            {
                DocumentId = current.DocumentId,
                RevisionId = current.RevisionId ?? Guid.Empty,
                RequestVersion = version,
                AccountId = current.AccountStableId,
                ProjectId = current.ProjectStableId,
                VoiceId = current.VoiceStableId,
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(request.CompiledPayload.Span)),
            });
        }, Invalidate);
    }

    private void AddPrivateDiagnosticsControl(MainWindow owner, StackPanel host, ShellViewModel model)
    {
        var button = NewButton("Private provider error diagnostics (off by default)");
        button.Click += async (_, _) => await OpenPrivateDiagnosticsAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One private modal owns its consent controls, cancellation and awaited tasks; redaction/persistence are separately tested services.")]
    private async Task OpenPrivateDiagnosticsAsync(MainWindow owner, ShellViewModel model)
    {
        if (_privateDialogOpen) return;
        _privateDialogOpen = true;
        using var lifetime = new CancellationTokenSource(); Task? active = null; Task? activeRead = null;
        try
        {
            var dialog = CreateArticleExportDialog(); dialog.Title = "PRIVATE - redacted provider error details";
            dialog.Closed += (_, _) => { lifetime.Cancel(); if (active is { IsCompleted: false }) privateDiagnostics.Disarm(); };
            var status = NewDescription(string.Empty);
            var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
            body.Children.Add(NewDescription("Private, possibly sensitive provider text. Known credentials and input echoes are redacted; anonymity is not guaranteed. Local only: excluded from normal logs, exports and uploads. Active records expire after 24 hours and are removed on store access; external copies may persist."));
            const string consentText = "Observe one response for this exact saved request; retain only HTTP400/INVALID_ARGUMENT messages. Sends and approves nothing.";
            var consent = new CheckBox { Content = new TextBlock { Text = consentText, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } };
            AutomationProperties.SetName(consent, consentText);
            var arm = NewButton("Enable once for selected saved request (10 minutes)"); arm.IsEnabled = false;
            var cancel = NewButton("Disable private capture"); var refresh = NewButton("Read private local records");
            var view = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, MaxHeight = 320 };
            AutomationProperties.SetName(view, "Private redacted provider messages, inert read-only text");
            async Task RefreshAsync()
            {
                refresh.IsEnabled = false;
                try
                {
                    status.Text = (privateDiagnostics.IsArmed ? "Enabled for one exact request; no paid permission is granted. " : "Capture is off. ") + privateDiagnostics.PersistenceStatus;
                    var records = await privateDiagnostics.ReadRecordsAsync(lifetime.Token).ConfigureAwait(true);
                    lifetime.Token.ThrowIfCancellationRequested();
                    view.Text = string.Join("\n\n", records.Select(x => $"PRIVATE {x.CreatedUtc:O} - request {x.PayloadSha256[..12]} - {(x.Truncated ? "truncated" : "bounded")}:\n{x.Message}"));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = "Private records could not be read. No generation state was changed."; }
                finally { if (!lifetime.IsCancellationRequested) refresh.IsEnabled = true; }
            }
            consent.IsCheckedChanged += (_, _) =>
            {
                if (consent.IsChecked != true) { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); }
                arm.IsEnabled = consent.IsChecked == true && active is not { IsCompleted: false };
            };
            arm.Click += (_, _) =>
            {
                if (consent.IsChecked == true) active = ArmPrivateDiagnosticsAsync(model, arm, status, () => consent.IsChecked == true, lifetime.Token);
            };
            cancel.Click += (_, _) => { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); consent.IsChecked = false; activeRead = RefreshAsync(); };
            refresh.Click += (_, _) => { if (activeRead is not { IsCompleted: false }) activeRead = RefreshAsync(); };
            foreach (Control c in new Control[] { status, consent, arm, cancel, refresh, view, CreateHistoricalCloseButton(dialog) }) body.Children.Add(c);
            dialog.Content = new ScrollViewer { Content = body }; activeRead = RefreshAsync();
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (active is not null) await active.ConfigureAwait(true);
            if (activeRead is not null) await activeRead.ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "Private diagnostic view is unavailable. No generation or financial state was changed."; }
        finally { _privateDialogOpen = false; }
    }

    private async Task ArmPrivateDiagnosticsAsync(ShellViewModel model, Button arm, TextBlock status, Func<bool> confirmed, CancellationToken token)
    {
        arm.IsEnabled = false;
        try
        {
            var binding = await new GooglePrivateDiagnosticOptIn(privateDiagnostics, privateDiagnosticCompilation)
                .PrepareAsync(model, confirmed, token).ConfigureAwait(true);
            if (binding is null) return;
            _privateCaptureUiBinding = binding;
            status.Text = "Enabled for ONE exact saved request, for 10 minutes. Close this view and use normal request review only if separately authorized. This action sent nothing.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { status.Text = "Private capture could not be enabled. Save the selected document and verify its current account/voice metadata. No request was sent."; }
    }
}
