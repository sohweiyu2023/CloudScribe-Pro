using System.Reflection;
using System.Xml.Linq;
using CloudScribe.App;

namespace CloudScribe.Architecture.Tests;

public sealed class RepositoryVersionContractTests
{
    [Fact]
    public void ExecutableInformationalVersionMatchesApplicationProjectVersion()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "CloudScribe.App.csproj");
        XDocument project = XDocument.Load(projectPath);
        string expected = project
            .Descendants("InformationalVersion")
            .Select(static element => element.Value.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidOperationException(
                "CloudScribe.App.csproj InformationalVersion is unavailable.");
        string actual = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException(
                "CloudScribe executable informational version is unavailable.");

        Assert.Equal(expected, actual);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))
                && File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "src",
                        "CloudScribe.App",
                        "CloudScribe.App.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CloudScribe repository root from the test working directory.");
    }
}
