using System.Xml.Linq;

namespace CloudScribe.Architecture.Tests;

public sealed class SafeDiagnosticExportReachabilityTests
{
    [Fact]
    public void DiagnosticsExposesSeparateAccessibleExportAndCancelWithTheFixedWorkspaceDestination()
    {
        string root = FindRoot();
        XNamespace ui = "https://github.com/avaloniaui";
        var view = XDocument.Load(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.axaml"));
        var section = Assert.Single(view.Descendants(ui + "StackPanel"), element =>
            string.Equals(element.Attribute("IsVisible")?.Value, "{Binding IsSafeDiagnosticExportVisible}", StringComparison.Ordinal));
        var buttons = section.Descendants(ui + "Button").ToArray();
        Assert.Contains(buttons, element => string.Equals(element.Attribute("Command")?.Value, "{Binding ExportSafeDiagnosticHistoryCommand}", StringComparison.Ordinal));
        Assert.Contains(buttons, element => string.Equals(element.Attribute("Command")?.Value, "{Binding CancelSafeDiagnosticHistoryCommand}", StringComparison.Ordinal));
        Assert.All(buttons, element => Assert.False(string.IsNullOrWhiteSpace(element.Attribute("Content")?.Value)));
        Assert.Contains(section.Descendants(), element => string.Equals(element.Attribute("Text")?.Value, "{Binding SafeDiagnosticExportDisclosure}", StringComparison.Ordinal));
        Assert.Contains(section.Descendants(), element => string.Equals(element.Attribute("Text")?.Value, "{Binding SafeDiagnosticExportDestination}", StringComparison.Ordinal));
        string composition = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "Composition", "CompositionRoot.cs"));
        Assert.Contains("ConfigureSafeDiagnosticExport(serviceProvider.GetRequiredService<AppPaths>().SupportBundleStagingDirectory)", composition, StringComparison.Ordinal);
        Assert.Contains("ConfigureStage5GenerationDiagnostics(", composition, StringComparison.Ordinal);
        string shell = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.SafeDiagnosticExport.cs"));
        Assert.Contains("Task.Run(() => _supportBundles.CreateAsync(destination, cancellation.Token), cancellation.Token)", shell, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }
}
