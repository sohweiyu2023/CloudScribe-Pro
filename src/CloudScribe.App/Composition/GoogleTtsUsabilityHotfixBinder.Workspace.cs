using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    [SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Local modal layout and event wiring; persistence and request inspection are separately tested services.")]
    private async Task OpenWorkspaceSettingsAsync(MainWindow owner, ShellViewModel model)
    {
        Window dialog = new()
        {
            Title = "Saved setups and local status",
            Width = 780,
            Height = 740,
            MinWidth = 560,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        StackPanel body = new() { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        TextBlock Text(string value) => new() { Text = value, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(Text("Documents autosave locally after a short pause. Ctrl+S creates a checkpoint; Library reopens saved documents."));
        TextBlock connection = Text("Reading saved connection metadata locally..."); body.Children.Add(connection);
        Button repair = NewButton("Repair existing Google connection");
        repair.IsEnabled = credentialRepair is not null;
        repair.Click += async (_, _) => await OpenCredentialRepairAsync(dialog, model, connection).ConfigureAwait(true);
        body.Children.Add(repair);
        body.Children.Add(Text("Named voice setups remember language and voice only. They never contain keys, text, prices, capability evidence or spend approval."));
        ComboBox saved = new() { PlaceholderText = "Choose a saved voice setup", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(saved, "Saved voice setups"); body.Children.Add(saved);
        TextBox name = NewTextBox("Name, for example AtR narration"); name.MaxLength = 60; AddField(body, "Voice setup name", name);
        TextBlock current = Text(string.Empty); body.Children.Add(current);
        TextBlock message = Text(preferences.Warning ?? "Select a verified voice in Studio, name it here and save. Loading a setup sends nothing."); body.Children.Add(message);
        Button save = NewButton("Save current voice setup");
        Button load = NewButton("Load selected setup"); Button remove = NewButton("Delete selected setup");
        load.IsEnabled = false; remove.IsEnabled = false;
        saved.SelectionChanged += (_, _) => { load.IsEnabled = saved.SelectedItem is string; remove.IsEnabled = load.IsEnabled; };
        name.TextChanged += (_, _) => save.IsEnabled = model.SelectedVoiceLabVoice is not null && !string.IsNullOrWhiteSpace(name.Text);
        WrapPanel actions = new() { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { save, load, remove }) actions.Children.Add(button);
        body.Children.Add(actions);
        void Update()
        {
            string? selected = saved.SelectedItem as string;
            saved.ItemsSource = preferences.State.Setups.Select(s => s.Name).ToArray();
            saved.SelectedItem = selected;
            current.Text = "Current voice: " + (model.SelectedVoiceLabVoice?.VoiceStableId ?? "not validated") +
                ". Remembered preference: " + (model.PreferredVoiceName ?? "none") + ". Refresh real Google voices before use.";
            save.IsEnabled = model.SelectedVoiceLabVoice is not null && !string.IsNullOrWhiteSpace(name.Text);
        }
        void TryChange(Action change)
        {
            try { change(); Update(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { message.Text = "Setup could not be changed. Check the name, disk access and the saved preferences file; existing data was preserved."; }
        }
        save.Click += (_, _) => TryChange(() =>
        {
            VoiceLabCatalogSelection voice = model.SelectedVoiceLabVoice ?? throw new InvalidOperationException("Select a voice first.");
            string locale = model.VoiceLabLocaleFilter ?? (voice.LanguageCodes is { Count: > 0 } codes ? codes[0] : null) ?? throw new InvalidOperationException("Select a language.");
            preferences.SaveSetup(new VoiceSetup((name.Text ?? string.Empty).Trim(), locale, voice.VoiceStableId));
            message.Text = "Voice setup saved locally. Reusing it never reuses approval.";
        });
        load.Click += (_, _) => TryChange(() =>
        {
            VoiceSetup? setup = preferences.State.Setups.FirstOrDefault(s => string.Equals(s.Name, saved.SelectedItem as string, StringComparison.Ordinal));
            if (setup is null) return;
            model.ApplySavedVoiceSetup(setup); preferences.Remember(setup);
            message.Text = "Loaded. Close this window and refresh real Google voices; the exact returned matching voice will be selected.";
        });
        remove.Click += (_, _) => TryChange(() =>
        {
            if (saved.SelectedItem is not string selected) return;
            preferences.DeleteSetup(selected); message.Text = "Named setup deleted. Your connection, documents and audio are unchanged.";
        });
        body.Children.Add(Text("Local audio stays on disk after restart. Play/export trust is session-only; use your player to open older MP3s without regenerating."));
        Button audio = NewButton("Open saved MP3 folder (no generation)");
        audio.Click += (_, _) => OpenLocalFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudScribe Pro", "Generated"), message);
        body.Children.Add(audio);
        Button workspace = NewButton("Open local workspace folder"); workspace.Click += (_, _) => OpenLocalFolder(paths.RootDirectory, message); body.Children.Add(workspace);
        body.Children.Add(Text("Persisted request status: local inspection only. This cannot prove whether a lost provider response incurred a charge."));
        TextBlock queue = Text("Reading local request states..."); body.Children.Add(new ScrollViewer { Content = queue, MaxHeight = 180 });
        Button refresh = NewButton("Refresh local request status (offline)"); body.Children.Add(refresh);
        async Task RefreshStatusAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                LocalRequestStatus status = await requestStatus.ReadAsync().ConfigureAwait(true);
                queue.Text = $"{status.Total} recorded request identities; {status.Unresolved} unresolved. Showing up to 100 records.\n" + string.Join('\n', status.Rows);
                if (status.Unresolved > 0)
                    queue.Text += "\nDo not reset or resubmit an uncertain request. Check provider billing/support for its outcome. Automatic reconciliation of a lost synchronous response is not available in this interface.";
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { queue.Text = "Local queue could not be read. No queue state was reset. Keep uncertain requests on hold."; }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);
        Button close = NewButton("Close saved setups"); close.Click += (_, _) => dialog.Close();
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = body });
        close.Margin = new Avalonia.Thickness(20, 8); Grid.SetRow(close, 1); layout.Children.Add(close);
        dialog.Content = layout;
        Update();
        await LoadConnectionStatusAsync(connection).ConfigureAwait(true);
        await RefreshStatusAsync().ConfigureAwait(true);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private async Task LoadConnectionStatusAsync(TextBlock label)
    {
        try
        {
            var configured = (await accounts.ListAsync().ConfigureAwait(true)).Where(a => a.IsEnabled && string.Equals(a.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)).ToArray();
            label.Text = configured.Length switch
            {
                0 => "No enabled Google connection saved. Configure Google once in Studio.",
                1 => "Google connection metadata is saved. This does not verify credentials or current Google access. Open Create MP3 to check the connection; use Repair existing Google connection if saved credentials are unavailable.",
                _ => "Multiple enabled Google connections exist. Refresh currently requires exactly one; do not add another account to fix this. Account selection/management needs a separate workflow.",
            };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { label.Text = "Saved connection metadata could not be read. No credentials were changed."; }
    }

    private static void OpenLocalFolder(string directory, TextBlock message)
    {
        try
        {
            if (!Directory.Exists(directory)) { message.Text = "This folder has no saved files yet."; return; }
            _ = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { message.Text = "Windows could not open the folder. Your files were left unchanged."; }
    }
}
