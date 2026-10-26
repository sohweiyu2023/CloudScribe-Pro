using System.Reflection;
using CloudScribe.App;

namespace CloudScribe.Architecture.Tests;

public sealed class NativeUiDiagnosticLogTests
{
    private static readonly Assembly AppAssembly = typeof(MainWindow).Assembly;

    [Theory]
    [InlineData("Import", "Open")]
    [InlineData("Import", "Cancel")]
    [InlineData("Export", "Complete")]
    [InlineData("Play", "ShellOpened")]
    public void ActionMetadataOmitsExceptionMessagesAndPaths(string operation, string phase)
    {
        const string secret = "PRIVATE-TOKEN-DOCUMENT-AUDIO-MARKER";
        Exception error = new InvalidOperationException($"{secret} C:\\Users\\private\\document.txt");
        string result = Format(operation, phase, error);
        Assert.Contains($"operation={operation}", result, StringComparison.Ordinal);
        Assert.Contains($"phase={phase}", result, StringComparison.Ordinal);
        Assert.Contains("exceptionType=System.InvalidOperationException", result, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", result, StringComparison.Ordinal);
        Assert.DoesNotContain("document.txt", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumValuesAreNotLoggedAsArbitraryInput()
    {
        string result = Format("999", "999", null);
        Assert.Contains("operation=Unknown; phase=Unknown", result, StringComparison.Ordinal);
        Assert.DoesNotContain("999", result, StringComparison.Ordinal);
        Assert.Contains("exceptionType=none; hresult=0", result, StringComparison.Ordinal);
    }

    private static string Format(string operation, string phase, Exception? error)
    {
        Type logger = AppAssembly.GetType("CloudScribe.App.Diagnostics.NativeUiDiagnosticLog", throwOnError: true)!;
        Type operationType = AppAssembly.GetType("CloudScribe.App.Diagnostics.NativeUiOperation", throwOnError: true)!;
        Type phaseType = AppAssembly.GetType("CloudScribe.App.Diagnostics.NativeUiPhase", throwOnError: true)!;
        MethodInfo formatter = logger.GetMethod("FormatAction", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)formatter.Invoke(null, [Enum.Parse(operationType, operation), Enum.Parse(phaseType, phase), error])!;
    }
}
