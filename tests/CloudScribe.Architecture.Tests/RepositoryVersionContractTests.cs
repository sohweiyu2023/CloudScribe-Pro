using System.Reflection;
using System.Text.Json;
using CloudScribe.App;

namespace CloudScribe.Architecture.Tests;

public sealed class RepositoryVersionContractTests
{
    [Fact]
    public void ExecutableInformationalVersionMatchesSessionStateRepositoryVersion()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument state = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "SESSION_STATE.json")));
        string expected = state.RootElement.GetProperty("repository_version").GetString()
            ?? throw new InvalidOperationException("SESSION_STATE.json repository_version is null.");
        string actual = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("CloudScribe executable informational version is unavailable.");

        Assert.Equal(expected, actual);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))
                && File.Exists(Path.Combine(directory.FullName, "SESSION_STATE.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CloudScribe repository root from the test working directory.");
    }
}
