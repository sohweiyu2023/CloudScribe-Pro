using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace CloudScribe.App.Composition;

public static class CloudScribeAbout
{
    public const string Website = "https://www.awakeningtoreality.com/";
    public const string PayPalEmail = "sohweiyu@outlook.com";
    public const string Credit = "Created and maintained by Soh Wei Yu";
    public const string SupportMessage = "CloudScribe Pro is free to use. If it saves you time and you would like to support continued maintenance, an optional PayPal contribution is appreciated - never required.";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One native About layout with clipboard and official website handlers.")]
    public static async Task ShowAsync(Window owner)
    {
        Window dialog = new()
        {
            Title = "About CloudScribe Pro",
            Width = 560,
            Height = 680,
            MinWidth = 360,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        StackPanel body = new() { Spacing = 16, Margin = new Thickness(26) };
        body.Children.Add(Text("CLOUDSCRIBE PRO", 25));
        body.Children.Add(Text("Version " + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(CloudScribeAbout).Assembly)?.InformationalVersion));
        body.Children.Add(Text(Credit, 18));
        body.Children.Add(Text(SupportMessage));
        body.Children.Add(Text("Awakening to Reality", 18));
        body.Children.Add(Text(Website));
        TextBlock status = Text(string.Empty);
        Button website = Button("Open Awakening to Reality website");
        website.Click += (_, _) =>
        {
            try { _ = Process.Start(new ProcessStartInfo(Website) { UseShellExecute = true }); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            { status.Text = "Your browser could not open. Visit " + Website; }
        };
        body.Children.Add(website);
        body.Children.Add(Text("Optional PayPal support", 18));
        body.Children.Add(new SelectableTextBlock { Text = PayPalEmail, Foreground = Brushes.LightSkyBlue });
        Button copy = Button("Copy PayPal email address");
        copy.Click += async (_, _) =>
        {
            try
            {
                if (dialog.Clipboard is not { } clipboard) throw new InvalidOperationException("Clipboard unavailable");
                await clipboard.SetTextAsync(PayPalEmail).ConfigureAwait(true);
                status.Text = "The PayPal email address was copied to the clipboard.";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { status.Text = "The email address could not be copied. You can copy it manually: " + PayPalEmail; }
        };
        body.Children.Add(copy);
        body.Children.Add(Text("Independent software. Not affiliated with or endorsed by Google or OpenAI. Google Cloud Text-to-Speech usage is billed by Google under the user's own account."));
        body.Children.Add(Text("About layout and optional support wording adapted from Cosmic Transcriber 1.0.27. CloudScribe Pro is a separate text-to-speech application."));
        body.Children.Add(status);
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = body });
        Button close = Button("Close About");
        close.Margin = new Thickness(24, 10);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 1);
        layout.Children.Add(close);
        dialog.Content = layout;
        DialogBounds.Fit(dialog, owner);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private static TextBlock Text(string value, double size = 14) => new()
    { Text = value, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label)
    {
        Button result = new() { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 };
        AutomationProperties.SetName(result, label);
        return result;
    }
}
