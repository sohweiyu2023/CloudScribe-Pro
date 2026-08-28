namespace CloudScribe.Architecture.Tests;

public sealed class Stage3PersistenceArchitectureTests
{
    [Fact]
    public void Stage3UsesOneMigratedApplicationDatabaseContext()
    {
        string root = RepositoryRoot();
        string persistence = Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence");
        string context = File.ReadAllText(Path.Combine(persistence, "CloudScribeDbContext.cs"));
        string initializer = File.ReadAllText(Path.Combine(persistence, "DatabaseInitializer.cs"));

        Assert.False(File.Exists(Path.Combine(persistence, "ObservabilityDbContext.cs")));
        Assert.Contains("DbSet<DocumentEntity>", context, StringComparison.Ordinal);
        Assert.Contains("DbSet<DocumentRevisionEntity>", context, StringComparison.Ordinal);
        Assert.Contains("DbSet<DocumentSectionEntity>", context, StringComparison.Ordinal);
        Assert.Contains("DbSet<TagEntity>", context, StringComparison.Ordinal);
        Assert.Contains("DbSet<BookmarkEntity>", context, StringComparison.Ordinal);
        Assert.Contains("DbSet<ReadingPositionEntity>", context, StringComparison.Ordinal);
        Assert.Contains("MigrateAsync", initializer, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCreatedAsync", initializer, StringComparison.Ordinal);
        Assert.Contains("PRAGMA integrity_check", initializer, StringComparison.Ordinal);
        Assert.Contains("PRAGMA foreign_key_check", initializer, StringComparison.Ordinal);
        Assert.Contains("PRAGMA journal_mode=WAL", initializer, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage3KeepsDocumentContentCommitsPhysicalHashedAndAtomic()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "CloudScribe.Infrastructure",
            "Files",
            "DocumentContentStore.cs"));

        Assert.Contains("PhysicalDirectoryPolicy", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("Flush(flushToDisk: true)", source, StringComparison.Ordinal);
        Assert.Contains("File.Move", source, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", source, StringComparison.Ordinal);
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
