namespace CloudScribe.Architecture.Tests;

public sealed class Stage4FoundationArchitectureTests
{
    [Fact]
    public void Stage4PricingRouteIsTruthfulAboutBlockedExactCatalogAdmission()
    {
        string root = RepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));
        string state = File.ReadAllText(Path.Combine(root, "SESSION_STATE.json"));

        Assert.Contains("Exact catalog contract not admitted", shell, StringComparison.Ordinal);
        Assert.Contains("schema 1.1.5/seed bytes required", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"stage4_catalog_contract_admitted\": false", state, StringComparison.Ordinal);
        Assert.DoesNotContain("requires final native Windows certification before promotion", state, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderControlsAreVisibleButExplainWhyTheyRemainDisabled()
    {
        string root = RepositoryRoot();
        string availability = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "Design", "StageFeatureAvailability.cs"));
        string window = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.axaml"));

        Assert.Contains("Stage4", availability, StringComparison.Ordinal);
        Assert.Contains("ShowProviderControls: true", availability, StringComparison.Ordinal);
        Assert.Contains("No admitted account", window, StringComparison.Ordinal);
        Assert.Contains("stay disabled with explicit reasons", window, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root could not be located from test output.");
    }
}
