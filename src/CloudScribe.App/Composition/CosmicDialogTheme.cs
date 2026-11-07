using Avalonia;
using Avalonia.Media;

namespace CloudScribe.App.Composition;

/// <summary>One palette for code-built dialogs and the guided walkthrough, matching the main window's
/// "cosmic studio" theme (violet surfaces, gold primary, cyan progress). New brush instances are returned
/// because Avalonia brushes are mutable and must not be shared across windows.</summary>
public static class CosmicDialogTheme
{
    public const string SurfaceTop = "#2B2040";
    public const string SurfaceMiddle = "#1C182B";
    public const string SurfaceBottom = "#181326";
    public const string Raised = "#272036";
    public const string BorderSubtle = "#40364D";
    public const string Border = "#766485";
    public const string Gold = "#E2BD77";
    public const string GoldText = "#1B1425";
    public const string Cyan = "#48BDEB";
    public const string CyanSoft = "#3348BDEB";
    public const string TextOnDark = "#FFF9F5";

    public static IBrush CreateDialogBackground() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(SurfaceTop), 0),
            new GradientStop(Color.Parse(SurfaceMiddle), 0.45),
            new GradientStop(Color.Parse(SurfaceBottom), 1),
        },
    };

    public static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));

    /// <summary>A raised, top-bordered bar for a dialog's fixed footer, so it reads as separate from the scrolling content above.</summary>
    public static Avalonia.Controls.Border ActionBar(Avalonia.Controls.Control content) => new()
    {
        Child = content,
        Background = Solid(Raised),
        BorderBrush = Solid(BorderSubtle),
        BorderThickness = new Thickness(0, 1, 0, 0),
        BoxShadow = new BoxShadows(new BoxShadow { OffsetY = -6, Blur = 18, Color = Color.Parse("#66000000") }),
    };

    /// <summary>Dialog heading: a gold eyebrow-style title and a plain subtitle, matching the main window's section headers.</summary>
    public static Avalonia.Controls.StackPanel Heading(string title, string subtitle)
    {
        var panel = new Avalonia.Controls.StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
        var heading = new Avalonia.Controls.TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Solid(TextOnDark),
            TextWrapping = TextWrapping.Wrap,
        };
        Avalonia.Automation.AutomationProperties.SetName(heading, title);
        panel.Children.Add(heading);
        panel.Children.Add(new Avalonia.Controls.Border { Height = 3, Width = 48, CornerRadius = new CornerRadius(2), Background = Solid(Gold), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new Avalonia.Controls.TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = Solid("#C6BCCF") });
        return panel;
    }

    /// <summary>WCAG relative-luminance contrast ratio, used by tests to keep text readable on these surfaces.</summary>
    public static double Contrast(string foreground, string background)
    {
        static double Channel(byte value)
        {
            double c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        double a = Luminance(Color.Parse(foreground)), b = Luminance(Color.Parse(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
