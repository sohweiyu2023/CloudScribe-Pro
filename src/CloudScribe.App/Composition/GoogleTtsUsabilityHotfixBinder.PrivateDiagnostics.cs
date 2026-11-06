using System.Security.Cryptography;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
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
    private async Task OpenPrivateDiagnosticsAsync(Window owner, ShellViewModel model, bool readOnly = false)
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
            body.Children.Add(NewDescription("Private, possibly sensitive provider text. Known credentials and input echoes are redacted; anonymity is not guaranteed. Selected-field records are unencrypted and inherit folder permissions; protected response records require Windows current-user protection. Local only: excluded from normal logs, exports and uploads. Records expire after 24 hours and are removed on store access; physical deletion while closed and external-copy cleanup are not guaranteed."));
            const string consentText = "I consent to one response for this exact saved request in the selected private capture mode. Sends and approves nothing.";
            const string envelopeText = "Use protected response evidence: bounded redacted errors across HTTP statuses, including malformed/partial responses. May retain article fragments. Windows current-user protection, not protection against administrators. Otherwise retain only HTTP400/INVALID_ARGUMENT explanation fields.";
            var detailed = new CheckBox { Content = new TextBlock { Text = envelopeText, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } };
            AutomationProperties.SetName(detailed, envelopeText);
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
                    status.Text = (readOnly ? "Viewing saved evidence; this viewer does not change job capture. " : privateDiagnostics.IsArmed ? "Enabled for one exact request; no paid permission is granted. " : "Capture is off. ") + privateDiagnostics.PersistenceStatus + TrendHistoryStatus();
                    var records = await privateDiagnostics.ReadRecordsAsync(lifetime.Token).ConfigureAwait(true);
                    lifetime.Token.ThrowIfCancellationRequested();
                    status.Text += privateDiagnostics.ReadStatus == PrivateProviderErrorReadStatus.Success
                        ? $" Read completed: {records.Count} available record(s)."
                        : $" Read did not complete ({privateDiagnostics.ReadStatus}); an empty view does not mean no response was retained. Keep existing files and inspect storage health.";
                    view.Text = string.Join("\n\n", records.Select(x => $"PRIVATE {x.CreatedUtc:O} - request {x.PayloadSha256[..12]} - {(x.Truncated ? "truncated" : "bounded")}{(x.ProviderDetailsOmitted is true ? "; other provider details omitted" : x.ProviderDetailsOmitted is null ? "; legacy detail completeness not recorded" : string.Empty)}:\n{x.Message}" +
                        (x.Response is { } response ? $"\nHTTP {response.HttpStatus}; {response.ErrorStatus}; {response.ReadState}; {response.ParseState}; read completed={response.ReadCompleted}; redaction={response.RedactionApplied}; omitted={response.DetailsOmitted}; truncated={response.Truncated}\n{response.RedactedBody}" : string.Empty)));
                }
                catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
                { _ = ReportLocalFailure(LocalRequestStage.CaptureRead, error); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = ReportLocalFailure(LocalRequestStage.CaptureRead, error); }
                finally { if (!lifetime.IsCancellationRequested) refresh.IsEnabled = true; }
            }
            consent.IsCheckedChanged += (_, _) =>
            {
                if (consent.IsChecked != true) { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); }
                arm.IsEnabled = consent.IsChecked == true && active is not { IsCompleted: false };
            };
            arm.Click += (_, _) =>
            {
                bool broad = detailed.IsChecked == true;
                if (consent.IsChecked == true) active = ArmPrivateDiagnosticsAsync(model, arm, status,
                    () => consent.IsChecked == true && (detailed.IsChecked == true) == broad, lifetime.Token,
                    broad ? PrivateErrorCaptureMode.ProtectedResponseEnvelope : PrivateErrorCaptureMode.SelectedExplanation);
            };
            detailed.IsCheckedChanged += (_, _) => { consent.IsChecked = false; _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); };
            cancel.Click += (_, _) => { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); consent.IsChecked = false; activeRead = RefreshAsync(); };
            refresh.Click += (_, _) => { if (activeRead is not { IsCompleted: false }) activeRead = RefreshAsync(); };
            foreach (Control c in new Control[] { status, detailed, consent, arm, cancel, refresh, view, CreateHistoricalCloseButton(dialog) }) body.Children.Add(c);
            if (readOnly) foreach (Control c in new Control[] { detailed, consent, arm, cancel }) c.IsVisible = false;
            dialog.Content = new ScrollViewer { Content = body }; activeRead = RefreshAsync();
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (active is not null) await active.ConfigureAwait(true);
            if (activeRead is not null) await activeRead.ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = ReportLocalFailure(LocalRequestStage.CaptureOpen, error); }
        finally { _privateDialogOpen = false; }
    }

    private async Task ArmPrivateDiagnosticsAsync(ShellViewModel model, Button arm, TextBlock status, Func<bool> confirmed, CancellationToken token,
        PrivateErrorCaptureMode mode = PrivateErrorCaptureMode.SelectedExplanation)
    {
        arm.IsEnabled = false;
        try
        {
            var binding = await new GooglePrivateDiagnosticOptIn(privateDiagnostics, (IGoogleRecoveryCurrentCompilation?)chirpPreparation ?? privateDiagnosticCompilation)
                .PrepareAsync(model, confirmed, token, mode).ConfigureAwait(true);
            if (binding is null)
            {
                _privateCaptureUiBinding = null; privateDiagnostics.Disarm();
                status.Text = ReportLocalFailure(LocalRequestStage.CaptureArm,
                    LocalRequestDiagnostics.Mark(new InvalidOperationException("Capture consent changed."), LocalRequestReason.CaptureConsentWithdrawn));
                return;
            }
            _privateCaptureUiBinding = binding;
            status.Text = "Enabled for ONE exact saved request, for 10 minutes. Close this view and use normal request review only if separately authorized. This action sent nothing.";
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); _ = ReportLocalFailure(LocalRequestStage.CaptureArm, error); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { _privateCaptureUiBinding = null; privateDiagnostics.Disarm(); status.Text = ReportLocalFailure(LocalRequestStage.CaptureArm, error); }
    }
}
