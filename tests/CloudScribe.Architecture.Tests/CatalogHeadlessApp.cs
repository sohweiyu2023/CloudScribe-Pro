using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace CloudScribe.Architecture.Tests;

// No production Application startup, service host, desktop platform or default profile.
public sealed class CatalogHeadlessApp : Avalonia.Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<CatalogHeadlessApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
