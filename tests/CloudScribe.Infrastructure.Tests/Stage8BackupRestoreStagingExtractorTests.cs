using System.IO.Compression;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8BackupRestoreStagingExtractorTests
{
    [Fact]
    public void Admitted_archive_extracts_only_inside_new_staging_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "backup.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("project/data.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("{\"ok\":true}");
            }

            var decision = new BackupRestoreDecision(true, "restore-admitted");
            var result = BackupRestoreStagingExtractor.ExtractAdmittedArchive(
                archivePath,
                Path.Combine(root, "staging"),
                decision,
                maximumExtractedBytes: 1024);

            Assert.Equal(1, result.FilesExtracted);
            Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "project", "data.json")));
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "staging")), Path.GetFullPath(result.StagingDirectory), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Non_admitted_archive_never_extracts()
    {
        var denied = new BackupRestoreDecision(false, "restore-manifest-not-authenticated");
        Assert.Throws<InvalidOperationException>(() => BackupRestoreStagingExtractor.ExtractAdmittedArchive(
            "missing.zip",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            denied));
    }
}
