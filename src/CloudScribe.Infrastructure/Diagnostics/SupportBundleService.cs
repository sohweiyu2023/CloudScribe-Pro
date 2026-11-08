using System.IO.Compression;
using System.Text.Json;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed partial class SupportBundleService(
    AppPaths paths,
    IOptions<CloudScribeOptions> options,
    TimeProvider timeProvider,
    ILogger<SupportBundleService> logger) : ISupportBundleService
{
    private const string Disclosure = "Only regenerated allowlisted diagnostic metadata, permanent safe trend history and a generated environment manifest are included. Legacy, malformed and unknown free-form messages and exception text are omitted; original local records are unchanged and may remain private. Source sizes are capture bounds; sanitized output can differ. All captured records are streamed without aggregate byte or record-count truncation. Oversized, malformed or incomplete records receive explicit omission markers. Cancelled or failed exports do not publish a partial archive. Documents, audio, databases, credentials, private provider captures and payloads are excluded.";
    private const string ManifestRelativePath = "support-bundle-manifest.json";
    private static readonly string[] ExcludedCategories =
    [
        "documents",
        "audio",
        "database",
        "credentials",
        "request-response-bodies",
    ];

    public Task<SupportBundlePreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        SupportBundleFile[] sourceFiles = EnumerateEligibleFiles(cancellationToken);
        byte[] manifestBytes = BuildManifest(sourceFiles, createdAtUtc);
        return Task.FromResult(BuildPreview(sourceFiles, manifestBytes.LongLength));
    }

    public async Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        SupportBundleFile[] sourceFiles = EnumerateEligibleFiles(cancellationToken);
        byte[] manifestBytes = BuildManifest(sourceFiles, createdAtUtc);

        string physicalDestinationDirectory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
            destinationDirectory,
            "Support bundles cannot be written through a symbolic-link or reparse-point directory.");
        (string finalPath, string stagingPath) = CreateOutputPaths(physicalDestinationDirectory, createdAtUtc);
        try
        {
            await WriteBundleAsync(stagingPath, sourceFiles, manifestBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, finalPath, overwrite: false);
            CloudScribeLog.Observe(() => CloudScribeLog.SupportBundleCreated(logger));
            return finalPath;
        }
        catch
        {
            DeleteStagingFile(stagingPath);
            throw;
        }
    }

    private async Task WriteBundleAsync(
        string stagingPath,
        SupportBundleFile[] sourceFiles,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        FileStream output = new(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (output.ConfigureAwait(false))
        {
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int index = 0; index < sourceFiles.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddCapturedFileAsync(archive, sourceFiles[index], ExportPath(sourceFiles[index], index), cancellationToken).ConfigureAwait(false);
                }

                ZipArchiveEntry manifest = archive.CreateEntry(ManifestRelativePath, CompressionLevel.Optimal);
                using Stream manifestStream = manifest.Open();
                await manifestStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private static (string FinalPath, string StagingPath) CreateOutputPaths(
        string destinationDirectory,
        DateTimeOffset createdAtUtc)
    {
        string uniqueSuffix = $"{createdAtUtc:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        string finalPath = Path.Combine(destinationDirectory, $"CloudScribe-Support-{uniqueSuffix}.zip");
        return (finalPath, finalPath + ".partial");
    }

    private static void DeleteStagingFile(string stagingPath)
    {
        try
        {
            File.Delete(stagingPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task AddCapturedFileAsync(
        ZipArchive archive,
        SupportBundleFile file,
        string exportPath,
        CancellationToken cancellationToken)
    {
        string fullSourcePath = ResolveSourcePath(file);
        FileInfo sourceInfo = new(fullSourcePath);
        if (!sourceInfo.Exists
            || sourceInfo.LinkTarget is not null
            || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Support bundles do not follow symbolic links or reparse points.");
        }

        ZipArchiveEntry entry = archive.CreateEntry(exportPath, CompressionLevel.Optimal);
        using Stream destination = entry.Open();
        using FileStream source = new(
            fullSourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SanitizeCapturedAsync(source, destination, file.SizeBytes, cancellationToken).ConfigureAwait(false);
    }

    private static SupportBundlePreview BuildPreview(SupportBundleFile[] sourceFiles, long manifestSizeBytes)
    {
        SupportBundleFile manifestFile = new(
            ManifestRelativePath,
            manifestSizeBytes,
            "generated-redacted-environment-manifest");
        SupportBundleFile[] previewFiles = [.. ExportFiles(sourceFiles), manifestFile];
        long total = previewFiles.Sum(static file => file.SizeBytes);
        return new SupportBundlePreview(
            previewFiles,
            total,
            ContainsDocuments: false,
            ContainsAudio: false,
            ContainsSecrets: false,
            Disclosure);
    }

    private static byte[] BuildManifest(SupportBundleFile[] sourceFiles, DateTimeOffset createdAtUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            product = "CloudScribe Pro",
            createdAtUtc,
            disclosure = Disclosure,
            files = ExportFiles(sourceFiles),
            exclusions = ExcludedCategories,
        });

    private static string ExportPath(SupportBundleFile file, int index) =>
        string.Equals(file.RelativePath, TrendRelativePath, StringComparison.Ordinal) ? "history/google-trend-history.jsonl"
            : FormattableString.Invariant($"logs/diagnostic-{index + 1:D4}.jsonl");

    private static SupportBundleFile[] ExportFiles(SupportBundleFile[] sourceFiles) => sourceFiles
        .Select((file, index) => new SupportBundleFile(ExportPath(file, index), file.SizeBytes, "sanitized-diagnostic-metadata"))
        .ToArray();


    private void EnsureDiagnosticsDirectoryIsPhysical()
    {
        DirectoryInfo directory = new(paths.DiagnosticsDirectory);
        if (directory.Exists
            && (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("Support bundles do not traverse a symbolic-link or reparse-point diagnostics directory.");
        }
    }

    private SupportBundleFile[] EnumerateEligibleFiles(CancellationToken token)
    {
        paths.EnsureDiagnosticsDirectory();
        EnsureDiagnosticsDirectoryIsPhysical();
        int maximumEntryCount = options.Value.SupportBundleMaximumFileCount;
        List<FileInfo> files = new(maximumEntryCount);
        int unexpectedEntryCount = 0;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (FileSystemInfo entry in new DirectoryInfo(paths.DiagnosticsDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (entry is not FileInfo file
                || !file.Name.StartsWith("cloudscribe-", comparison)
                || (!file.Name.EndsWith(".jsonl", comparison)
                    && !file.Name.EndsWith(".log", comparison))
                || file.LinkTarget is not null
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (++unexpectedEntryCount > maximumEntryCount)
                    throw new InvalidOperationException("Support bundle diagnostics contain too many unexpected directory entries; inspect the directory before exporting.");
                continue;
            }

            files.Add(file);
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var captured = files
            .Select(file => new SupportBundleFile(
                $"logs/{file.Name}",
                file.Length,
                file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? "bounded-redacted-structured-diagnostic-log"
                    : "bounded-redacted-bootstrap-diagnostic-log"))
            .ToList();
        AddTrendSource(captured);
        return captured.ToArray();
    }
}
