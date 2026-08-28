using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed class SupportBundleService(
    AppPaths paths,
    IOptions<CloudScribeOptions> options,
    TimeProvider timeProvider,
    ILogger<SupportBundleService> logger) : ISupportBundleService
{
    private const string Disclosure = "Only bounded redacted diagnostic JSONL files, bounded redacted bootstrap text logs and a generated environment manifest are included. Documents, audio, databases, credentials and provider payloads are excluded.";
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
        SupportBundleFile[] sourceFiles = EnumerateEligibleFiles();
        byte[] manifestBytes = BuildManifest(sourceFiles, createdAtUtc);
        return Task.FromResult(BuildPreview(sourceFiles, manifestBytes.LongLength));
    }

    public async Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        SupportBundleFile[] sourceFiles = EnumerateEligibleFiles();
        byte[] manifestBytes = BuildManifest(sourceFiles, createdAtUtc);
        long cap = GetBundleCapBytes();
        EnsureInputFitsCap(sourceFiles, manifestBytes.LongLength, cap);

        string physicalDestinationDirectory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
            destinationDirectory,
            "Support bundles cannot be written through a symbolic-link or reparse-point directory.");
        (string finalPath, string stagingPath) = CreateOutputPaths(physicalDestinationDirectory, createdAtUtc);
        try
        {
            await WriteBundleAsync(stagingPath, sourceFiles, manifestBytes, cap, cancellationToken).ConfigureAwait(false);
            EnsureCompressedBundleFitsCap(stagingPath, cap);
            File.Move(stagingPath, finalPath, overwrite: false);
            CloudScribeLog.SupportBundleCreated(logger);
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
        long cap,
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
            using (CappedWriteStream boundedOutput = new(output, cap))
            using (ZipArchive archive = new(boundedOutput, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (SupportBundleFile file in sourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddCapturedFileAsync(archive, file, cancellationToken).ConfigureAwait(false);
                }

                ZipArchiveEntry manifest = archive.CreateEntry(ManifestRelativePath, CompressionLevel.Optimal);
                using Stream manifestStream = manifest.Open();
                await manifestStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private long GetBundleCapBytes() => options.Value.SupportBundleMaximumMiB * 1024L * 1024L;

    private static void EnsureInputFitsCap(
        SupportBundleFile[] sourceFiles,
        long manifestSizeBytes,
        long cap)
    {
        SupportBundlePreview preview = BuildPreview(sourceFiles, manifestSizeBytes);
        if (preview.TotalSizeBytes > cap)
        {
            throw new InvalidOperationException("Support bundle input exceeds the configured size cap.");
        }
    }

    private static void EnsureCompressedBundleFitsCap(string stagingPath, long cap)
    {
        if (new FileInfo(stagingPath).Length > cap)
        {
            throw new InvalidOperationException("Compressed support bundle exceeds the configured size cap.");
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
        CancellationToken cancellationToken)
    {
        EnsureDiagnosticsDirectoryIsPhysical();
        const string logPrefix = "logs/";
        if (!file.RelativePath.StartsWith(logPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A support-bundle diagnostic entry has an invalid relative path.");
        }

        string fileName = file.RelativePath[logPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A support-bundle diagnostic filename is not a single physical path segment.");
        }

        string sourcePath = Path.Combine(paths.DiagnosticsDirectory, fileName);
        string diagnosticsRoot = Path.GetFullPath(paths.DiagnosticsDirectory) + Path.DirectorySeparatorChar;
        string fullSourcePath = Path.GetFullPath(sourcePath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullSourcePath.StartsWith(diagnosticsRoot, pathComparison))
        {
            throw new InvalidOperationException("A support-bundle source escaped the diagnostics directory.");
        }

        FileInfo sourceInfo = new(fullSourcePath);
        if (!sourceInfo.Exists
            || sourceInfo.LinkTarget is not null
            || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Support bundles do not follow symbolic links or reparse points.");
        }

        ZipArchiveEntry entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
        using Stream destination = entry.Open();
        using FileStream source = new(
            fullSourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyExactlyAsync(source, destination, file.SizeBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long byteCount,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long remaining = byteCount;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("A diagnostic file changed before its captured bytes could be copied.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static SupportBundlePreview BuildPreview(SupportBundleFile[] sourceFiles, long manifestSizeBytes)
    {
        SupportBundleFile manifestFile = new(
            ManifestRelativePath,
            manifestSizeBytes,
            "generated-redacted-environment-manifest");
        SupportBundleFile[] previewFiles = [.. sourceFiles, manifestFile];
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
            files = sourceFiles,
            exclusions = ExcludedCategories,
        });


    private void EnsureDiagnosticsDirectoryIsPhysical()
    {
        DirectoryInfo directory = new(paths.DiagnosticsDirectory);
        if (directory.Exists
            && (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("Support bundles do not traverse a symbolic-link or reparse-point diagnostics directory.");
        }
    }

    private sealed class CappedWriteStream(Stream inner, long maximumLength) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                EnsurePosition(value);
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            EnsurePosition(target);
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            EnsurePosition(value);
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWrite(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWrite(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWrite(count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWrite(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The caller owns and durably flushes the underlying FileStream.
            base.Dispose(disposing);
        }

        private void EnsureWrite(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            long projectedLength = Math.Max(Length, checked(Position + count));
            if (projectedLength > maximumLength)
            {
                throw new IOException(
                    $"Compressed support bundle would exceed its configured {maximumLength}-byte hard cap.");
            }
        }

        private void EnsurePosition(long value)
        {
            if (value < 0 || value > maximumLength)
            {
                throw new IOException(
                    $"Compressed support bundle attempted to address byte {value} outside its {maximumLength}-byte hard cap.");
            }
        }
    }

    private SupportBundleFile[] EnumerateEligibleFiles()
    {
        paths.EnsureDiagnosticsDirectory();
        EnsureDiagnosticsDirectoryIsPhysical();
        int maximumEntryCount = options.Value.SupportBundleMaximumFileCount;
        List<FileInfo> files = new(maximumEntryCount);
        int examinedEntryCount = 0;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (FileSystemInfo entry in new DirectoryInfo(paths.DiagnosticsDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            examinedEntryCount++;
            if (examinedEntryCount > maximumEntryCount)
            {
                throw new InvalidOperationException(
                    $"Support bundle diagnostics contain more than the configured {maximumEntryCount} directory entries.");
            }

            if (entry is not FileInfo file
                || !file.Name.StartsWith("cloudscribe-", comparison)
                || (!file.Name.EndsWith(".jsonl", comparison)
                    && !file.Name.EndsWith(".log", comparison))
                || file.LinkTarget is not null
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            files.Add(file);
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return files
            .Select(file => new SupportBundleFile(
                $"logs/{file.Name}",
                file.Length,
                file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? "bounded-redacted-structured-diagnostic-log"
                    : "bounded-redacted-bootstrap-diagnostic-log"))
            .ToArray();
    }
}
