using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static void AddLocalPartsControls(MainWindow owner, StackPanel host, ShellViewModel model)
    {
        TextBlock size = NewDescription(string.Empty);
        Button parts = NewButton("Preview local narration parts (no generation)");
        void Update()
        {
            int bytes = Encoding.UTF8.GetByteCount(model.DocumentText);
            size.Text = $"Document: {bytes:N0} UTF-8 bytes. Short synthesis must also fit a 5,000-byte compiled envelope, including JSON escaping and request fields. Larger text needs parts.";
            parts.IsEnabled = model.HasOpenDocument && !string.IsNullOrWhiteSpace(model.DocumentText) && model.CanPrepareGoogleGenerationSpend;
        }
        model.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ShellViewModel.DocumentText) or nameof(ShellViewModel.HasOpenDocument) or nameof(ShellViewModel.CanPrepareGoogleGenerationSpend)) Update(); };
        parts.Click += async (_, _) => await PreviewLocalPartsAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(size); host.Children.Add(parts); Update();
    }

    private static async Task PreviewLocalPartsAsync(MainWindow owner, ShellViewModel model)
    {
        string original = model.DocumentText;
        string language = model.VoiceLabLocaleFilter ?? "en-US";
        string voice = model.SelectedVoiceLabVoice?.VoiceStableId ?? model.PreferredVoiceName ?? "en-US-Standard-C";
        Window dialog = new() { Title = "Local narration parts", Width = 640, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ThemeVariant.Dark };
        StackPanel body = new() { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        TextBlock status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }; body.Children.Add(status);
        Button create = NewButton("Create these local documents"); body.Children.Add(create);
        Button close = NewButton("Close local narration parts"); close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        try
        {
            if (voice.Contains("-Chirp3-HD-", StringComparison.OrdinalIgnoreCase))
            {
                var preview = CloudScribe.Application.Generation.ChirpNarrationPlanner.Plan(original);
                status.Text = $"{preview.Count} text-only internal requests proposed with a local 333-scalar boundary heuristic; this is not a Google limit or acceptance guarantee. No documents will be created. This preview does not inspect accepted audio. Use Internal article narration for independently reviewed coverage, accepted-audio reuse and request approval.";
                create.IsVisible = false; dialog.Content = body;
                await dialog.ShowDialog(owner).ConfigureAwait(true); return;
            }
            IReadOnlyList<string> chunks = LocalNarrationChunker.Split(original, language, voice);
            status.Text = $"{chunks.Count} parts planned for {voice}, {language}. The original stays unchanged and every character is retained. Each part needs its own current voice/price checks and explicit approval before synthesis. This action creates text documents only.";
            create.Click += async (_, _) =>
            {
                create.IsEnabled = false;
                try { int count = await model.CreateLocalNarrationPartsAsync(chunks, original).ConfigureAwait(true); status.Text = count == 0 ? "Existing verified parts were reused; no documents or audio were generated." : $"Created {count} parts. Expand their original article in Library; no synthesis was sent."; }
                catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                { status.Text = "Could not finish creating all parts. Check Library for parts already saved; the original remains unchanged. Preview again only after checking for duplicates."; }
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { status.Text = "This source could not be divided safely. Use a shorter section and a valid language/voice, then try again. No documents or audio were created."; create.IsEnabled = false; }
        dialog.Content = body;
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}
