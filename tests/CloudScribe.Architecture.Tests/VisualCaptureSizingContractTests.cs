using System;
using System.IO;
using Xunit;

namespace CloudScribe.Architecture.Tests;

public sealed class VisualCaptureSizingContractTests
{
    [Fact]
    public void VisualEvidenceRendersRequestedCaseSizeInsteadOfNativeWindowBounds()
    {
        string repositoryRoot = FindRepositoryRoot();
        string capture = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "MainWindow.VisualCapture.cs"));

        Assert.Contains("CaptureWindow(path, captureCase.Width, captureCase.Height)", capture, StringComparison.Ordinal);
        Assert.Contains("Math.Ceiling(width)", capture, StringComparison.Ordinal);
        Assert.Contains("Math.Ceiling(height)", capture, StringComparison.Ordinal);
        Assert.Contains("Content is not Control captureRoot", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Width = targetSize.Width;", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Height = targetSize.Height;", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Measure(targetSize);", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Arrange(new Rect(0, 0, targetSize.Width, targetSize.Height));", capture, StringComparison.Ordinal);
        Assert.Contains("bitmap.Render(captureRoot);", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Width = previousWidth;", capture, StringComparison.Ordinal);
        Assert.Contains("captureRoot.Height = previousHeight;", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Ceiling(Bounds.Width)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Ceiling(Bounds.Height)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("bitmap.Render(this);", capture, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CloudScribe.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate CloudScribe repository root from the architecture test output directory.");
    }
}
