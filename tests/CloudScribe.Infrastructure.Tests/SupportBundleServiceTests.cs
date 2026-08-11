using System.IO.Compression;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SupportBundleServiceTests
{
    [Fact]
    public async Task PreviewAndCreateIncludeOnlyRedactedDiagnosticsAndManifest()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        string diagnosticPath = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-20260721.jsonl");
        await File.WriteAllTextAsync(
            diagnosticPath,
            "{\"Message\":\"safe\"}\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            paths.DatabasePath,
            "database-secret",
            TestContext.Current.CancellationToken);
        SupportBundleService service = CreateService(paths, 8);

        Application.Diagnostics.SupportBundlePreview preview = await service.PreviewAsync(TestContext.Current.CancellationToken);
        string bundlePath = await service.CreateAsync(
            Path.Combine(temporary.Path, "exports"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, preview.Files.Count);
        Assert.Contains(preview.Files, file => file.RelativePath.EndsWith(".jsonl", StringComparison.Ordinal));
        Assert.Contains(preview.Files, file => file.RelativePath.StartsWith("logs/", StringComparison.Ordinal));
        Assert.Contains(preview.Files, file => string.Equals(file.RelativePath, "support-bundle-manifest.json", StringComparison.Ordinal));
        Assert.False(preview.ContainsDocuments);
        Assert.False(preview.ContainsAudio);
        Assert.False(preview.ContainsSecrets);

        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("cloudscribe.db", StringComparison.Ordinal));
        ZipArchiveEntry manifest = Assert.Single(archive.Entries, entry => string.Equals(entry.FullName, "support-bundle-manifest.json", StringComparison.Ordinal));
        using Stream manifestStream = manifest.Open();
        using JsonDocument manifestJson = await JsonDocument.ParseAsync(
            manifestStream,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("CloudScribe Pro", manifestJson.RootElement.GetProperty("product").GetString());
    }

    [Fact]
    public async Task ExcludesSymbolicLinkDiagnosticFilesWhenSupported()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        string externalFile = Path.Combine(temporary.Path, "outside.jsonl");
        await File.WriteAllTextAsync(
            externalFile,
            "{\"Message\":\"outside\"}\n",
            TestContext.Current.CancellationToken);
        string linkedDiagnostic = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-linked.jsonl");
        if (!TryCreateFileSymbolicLink(linkedDiagnostic, externalFile))
        {
            return;
        }

        SupportBundleService service = CreateService(paths, 8);
        Application.Diagnostics.SupportBundlePreview preview = await service.PreviewAsync(TestContext.Current.CancellationToken);
        string bundlePath = await service.CreateAsync(
            Path.Combine(temporary.Path, "exports"),
            TestContext.Current.CancellationToken);

        Assert.Single(preview.Files);
        Assert.Equal("support-bundle-manifest.json", preview.Files[0].RelativePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Single(archive.Entries);
        Assert.Equal("support-bundle-manifest.json", archive.Entries[0].FullName);
    }

    [Fact]
    public async Task RejectsSymbolicLinkDiagnosticsDirectoryWhenSupported()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        Directory.Delete(paths.DiagnosticsDirectory);
        string externalDirectory = Path.Combine(temporary.Path, "external-diagnostics");
        Directory.CreateDirectory(externalDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(externalDirectory, "cloudscribe-outside.jsonl"),
            "outside",
            TestContext.Current.CancellationToken);
        if (!TryCreateDirectorySymbolicLink(paths.DiagnosticsDirectory, externalDirectory))
        {
            return;
        }

        SupportBundleService service = CreateService(paths, 8);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PreviewAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(
                Path.Combine(temporary.Path, "exports"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsSymbolicLinkOutputDirectoryWhenSupported()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-safe.jsonl"),
            "{}\n",
            TestContext.Current.CancellationToken);
        string physicalOutput = Path.Combine(temporary.Path, "physical-output");
        Directory.CreateDirectory(physicalOutput);
        string linkedOutput = Path.Combine(temporary.Path, "linked-output");
        if (!TryCreateDirectorySymbolicLink(linkedOutput, physicalOutput))
        {
            return;
        }

        SupportBundleService service = CreateService(paths, 8);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(linkedOutput, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(physicalOutput));
    }

    [Fact]
    public async Task RejectsInputAboveConfiguredCapWithoutLeavingPartialArchive()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        string diagnosticPath = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-oversized.jsonl");
        await File.WriteAllBytesAsync(
            diagnosticPath,
            new byte[1024 * 1024 + 128],
            TestContext.Current.CancellationToken);
        SupportBundleService service = CreateService(paths, 1);
        string exportDirectory = Path.Combine(temporary.Path, "exports");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(exportDirectory, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(exportDirectory));
    }



    [Fact]
    public async Task RejectsExcessiveDiagnosticFileCountBeforeBuildingPreviewOrArchive()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        for (int index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(paths.DiagnosticsDirectory, $"cloudscribe-{index}.jsonl"),
                "{}\n",
                TestContext.Current.CancellationToken);
        }

        SupportBundleService service = CreateService(paths, 8, maximumFileCount: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PreviewAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(
                Path.Combine(temporary.Path, "exports"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsExcessiveUnrelatedDirectoryEntriesBeforeFiltering()
    {
        await using TemporaryDirectory temporary = new();
        AppPaths paths = CreatePaths(temporary.Path);
        paths.EnsureDirectories();
        for (int index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(paths.DiagnosticsDirectory, $"unrelated-{index}.tmp"),
                "ignored",
                TestContext.Current.CancellationToken);
        }

        SupportBundleService service = CreateService(paths, 8, maximumFileCount: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PreviewAsync(TestContext.Current.CancellationToken));
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static AppPaths CreatePaths(string root) => new(Options.Create(new CloudScribeOptions
    {
        AppDataDirectoryOverride = Path.Combine(root, "appdata"),
    }));

    private static SupportBundleService CreateService(
        AppPaths paths,
        int capMiB,
        int maximumFileCount = 256) => new(
        paths,
        Options.Create(new CloudScribeOptions
        {
            SupportBundleMaximumMiB = capMiB,
            SupportBundleMaximumFileCount = maximumFileCount,
        }),
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)),
        NullLogger<SupportBundleService>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudscribe-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
