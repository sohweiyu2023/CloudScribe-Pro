using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static void AddVoiceSampleLinks(StackPanel host, ShellViewModel model, Window lifetime)
    {
        host.Children.Add(NewDescription("Hear a voice before choosing. Google's public prerecorded samples open in your browser; CloudScribe sends no document or key and creates no billed audio."));
        Button table = NewButton("Hear voice samples - Google (browser)");
        table.Classes.Add("primary");
        Button direct = NewButton("Hear selected voice sample - Google (browser)");
        Button copy = NewButton("Copy selected voice name");
        TextBlock hint = NewDescription(string.Empty);
        void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            { hint.Text = "Your browser could not open. Visit " + OfficialGoogleVoiceSamples.VoiceTableUrl + " and find your voice name. No audio was generated."; }
        }
        void Update()
        {
            string? voice = model.SelectedVoiceLabVoice?.VoiceStableId;
            direct.IsEnabled = OfficialGoogleVoiceSamples.SampleUrlFor(voice) is not null;
            copy.IsEnabled = voice is not null;
            hint.Text = voice is null
                ? "You can browse Google's samples before connecting. Select a verified voice to copy its name or enable a checked direct sample link."
                : direct.IsEnabled
                    ? "Direct link: Google's prerecorded example for " + voice + ". For other examples, open the voice table. These examples do not read your document or change your Studio voice."
                    : "No direct sample link has been verified here for " + voice + ". Copy its name, open Google's voice table, press Ctrl+F and paste. Use the audio player in its row if available; a generic demo may use a different voice.";
        }
        table.Click += (_, _) => Open(OfficialGoogleVoiceSamples.VoiceTableUrl);
        direct.Click += (_, _) => { if (OfficialGoogleVoiceSamples.SampleUrlFor(model.SelectedVoiceLabVoice?.VoiceStableId) is { } url) Open(url); };
        copy.Click += async (_, _) =>
        {
            if (model.SelectedVoiceLabVoice?.VoiceStableId is not { } voice || lifetime.Clipboard is not { } clipboard) return;
            try { await clipboard.SetTextAsync(voice).ConfigureAwait(true); hint.Text = "Copied " + voice + ". In Google's voice table, press Ctrl+F and paste to find its sample."; }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            { hint.Text = "Copy was unavailable. Use the selected voice name shown above to find its row in Google's table."; }
        };
        Observe(host, model, (_, args) => { if (string.Equals(args.PropertyName, nameof(ShellViewModel.SelectedVoiceLabVoice), StringComparison.Ordinal)) Update(); }, lifetime);
        host.Children.Add(table); host.Children.Add(direct); host.Children.Add(copy); host.Children.Add(hint);
        Update();
    }
}
