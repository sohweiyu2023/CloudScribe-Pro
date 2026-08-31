using System.IO.Compression;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8BackupRestoreArchiveInspectorTests
{
    [Fact]
    public void OversizedDeclaredEntryFailsArchiveStructureAdmission()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "backup.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("project/data.bin", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write([1, 2, 3]);
        }

        var inspection = BackupRestoreArchiveInspector.Inspect(
            archivePath,
            maximumSingleEntryBytes: 2,
            maximumDeclaredUncompressedBytes: 100,
            maximumCompressionRatio: 1000);

        Assert.False(inspection.ArchiveStructureValid);
    }

    [Fact]
    public void ExcessiveCompressionRatioFailsClosed()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "backup.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("project/data.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(new string('A', 8192));
        }

        var inspection = BackupRestoreArchiveInspector.Inspect(
            archivePath,
            maximumSingleEntryBytes: 1_000_000,
            maximumDeclaredUncompressedBytes: 1_000_000,
            maximumCompressionRatio: 2);

        Assert.False(inspection.ArchiveStructureValid);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cloudscribe-stage8-inspect-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
