using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace CloudScribe.App.Composition;

public static class CloudScribeNoticesDialog
{
    public static async Task ShowAsync(Window owner)
    {
        IReadOnlyDictionary<string, string> documents = CloudScribeThirdPartyNotices.ReadDocuments();
        Window dialog = new()
        {
            Title = "Third-party licenses and notices",
            Width = 800,
            Height = 640,
            MinWidth = 360,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = "Read or copy the bundled notices and original license texts below. The separate .NET runtime's notices are in the package's runtime folder.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12),
        });
        ComboBox selector = new() { ItemsSource = documents.Keys, SelectedItem = CloudScribeThirdPartyNotices.IndexName, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(selector, "License or notices document");
        Grid.SetRow(selector, 1);
        layout.Children.Add(selector);
        TextBox text = CreateReader(documents[CloudScribeThirdPartyNotices.IndexName]);
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not string name || !documents.TryGetValue(name, out string? content)) return;
            text.Text = content;
            text.CaretIndex = 0;
        };
        Grid.SetRow(text, 2);
        layout.Children.Add(text);
        Button close = new() { Content = "Close notices", IsDefault = true, IsCancel = true, MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(close, "Close notices");
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 3);
        layout.Children.Add(close);
        dialog.Content = layout;
        DialogBounds.Fit(dialog, owner);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private static TextBox CreateReader(string content)
    {
        TextBox text = new()
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(text, "License and notices text");
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        return text;
    }
}
