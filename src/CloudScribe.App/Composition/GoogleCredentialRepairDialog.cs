using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed class GoogleCredentialRepairDialog : Window, IDisposable
{
    private readonly GoogleCredentialRepairController _controller;
    private readonly TextBox _email = new() { PlaceholderText = "Intended existing service-account email" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _consent = new() { IsChecked = false };
    private readonly Button _select = new() { Content = "Select existing private JSON locally" };
    private readonly Button _verify = new() { Content = "Verify intended connection with Google (no speech)" };
    private readonly Button _confirm = new() { Content = "Restore this connection locally once", IsEnabled = false };
    public bool MutationAttempted => _controller.MutationAttempted;
    public bool Committed => _controller.Committed;

    [SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Single modal layout; secret lifetime and transactional consent are separately tested.")]
    public GoogleCredentialRepairDialog(GoogleCredentialRepairService service, string accountId)
    {
        _controller = new(service, accountId);
        Title = "Repair existing Google connection - no speech";
        Width = 720; Height = 680; MinWidth = 560; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#111C2B"));
        StackPanel body = new() { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
        body.Children.Add(Text("Existing local account: " + accountId));
        body.Children.Add(Text("Reconnect an explicitly chosen existing service account. Historical key equality remains unknown. No new Google key/account, synthesis, upload or spending approval is included."));
        body.Children.Add(Text("The private JSON stays transiently in this app and is never shown here. Verify sends credentials only to the admitted Google OAuth/API endpoints. Selecting a file does not write credentials or contact Google."));
        body.Children.Add(_email); body.Children.Add(_select); body.Children.Add(_verify);
        _message.Foreground = Brushes.White; body.Children.Add(_message);
        _consent.Content = Text("I approve this exact local credential write to my Windows Credential Manager. No speech or key rotation.");
        body.Children.Add(_consent); body.Children.Add(_confirm);
        Button close = new() { Content = "Cancel / close repair" }; body.Children.Add(close);
        Content = new ScrollViewer { Content = body };
        AutomationProperties.SetName(_email, "Intended existing service-account email");
        AutomationProperties.SetName(_message, "Credential repair status");
        AutomationProperties.SetName(_consent, "Approve exact local credential write");
        _email.TextChanged += (_, _) => { _controller.IntendedEmail = _email.Text ?? string.Empty; ResetConsent(); };
        _consent.IsCheckedChanged += (_, _) => { _controller.Acknowledged = _consent.IsChecked == true; _confirm.IsEnabled = _controller.Review is not null && _controller.Acknowledged; };
        _select.Click += async (_, _) => await SelectAsync().ConfigureAwait(true);
        _verify.Click += async (_, _) => await VerifyAsync().ConfigureAwait(true);
        _confirm.Click += async (_, _) => await ConfirmAsync().ConfigureAwait(true);
        close.Click += (_, _) => Close();
        Closed += (_, _) => Dispose();
    }

    private void ResetConsent()
    {
        _consent.IsChecked = false; _confirm.IsEnabled = false;
        _message.Text = "Verify this exact candidate and intended identity before any local write.";
    }

    private async Task SelectAsync()
    {
        _controller.BeginCandidateSelection();
        ResetConsent(); Busy(true);
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the existing private service-account JSON",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Service-account JSON") { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if (files.Count == 0) return;
            using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[16 * 1024 + 1];
            try
            {
                int count = await reader.ReadBlockAsync(buffer.AsMemory()).ConfigureAwait(true);
                if (count == 0 || count == buffer.Length) throw new InvalidDataException("Candidate size is unsupported.");
                _controller.SelectCandidate(buffer.AsMemory(0, count));
                ResetConsent();
                _message.Text = "Private local candidate selected; contents hidden. Enter the intended service-account email, then verify.";
            }
            finally { Array.Clear(buffer); }
        }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        { _message.Text = "Candidate could not be read. No credential write was requested."; }
        finally { Busy(false); }
    }

    private async Task VerifyAsync()
    {
        Busy(true); ResetConsent();
        try
        {
            await _controller.VerifyAsync().ConfigureAwait(true);
            var review = _controller.Review!;
            _message.Text = $"Google authenticated the intended service account {review.Identity.ClientEmail} and project access {review.Identity.ProjectId}. Candidate declares key ID {review.Identity.PrivateKeyId}.\n"
                + $"Existing account {review.AccountId}; new local credential generation; old binding/history retained. Review expires {review.ExpiresAtUtc:u}.\n"
                + "Historical key equality is unknown. Confirm below to write locally once. Partial staging is retained if interrupted; do not retry blindly.";
        }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        { _message.Text = "Candidate verification stopped. No local credentials written. " + VoiceCatalogErrorMessages.For(error); }
        finally { Busy(false); }
    }

    private async Task ConfirmAsync()
    {
        Busy(true);
        try
        {
            await _controller.ConfirmAsync().ConfigureAwait(true);
            _message.Text = "Local reconnection committed. All previous holds remain. Close and reopen CloudScribe, then refresh real Google voices. No speech or new spending approved.";
        }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        { _message.Text = "No committed result was confirmed here. Retain staging/history; inspect repair status before resuming. No speech requested."; }
        finally { _consent.IsChecked = false; Busy(false); if (_controller.MutationAttempted) _confirm.IsEnabled = false; }
    }

    private void Busy(bool busy)
    {
        _email.IsEnabled = !busy && !_controller.MutationAttempted;
        _select.IsEnabled = _email.IsEnabled; _verify.IsEnabled = _email.IsEnabled;
        _consent.IsEnabled = !busy && _controller.Review is not null;
        _confirm.IsEnabled = !busy && _controller.Review is not null && _controller.Acknowledged;
    }

    public void Dispose() => _controller.Dispose();
}
