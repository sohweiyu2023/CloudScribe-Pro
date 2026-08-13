namespace CloudScribe.Architecture.Tests;

public sealed class Stage3CompletionArchitectureTests
{
    [Fact]
    public void Stage3WorkspaceUsesDurableDocumentStateAndRecoveryAwareShortcuts()
    {
        string root = RepositoryRoot();
        string viewModels = Path.Combine(root, "src", "CloudScribe.App", "ViewModels");
        string state = File.ReadAllText(Path.Combine(viewModels, "ShellViewModel.Documents.State.cs"));
        string save = File.ReadAllText(Path.Combine(viewModels, "ShellViewModel.Documents.Save.cs"));
        string import = File.ReadAllText(Path.Combine(viewModels, "ShellViewModel.Documents.Import.cs"));
        string behavior = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "DocumentWindowBehavior.cs"));
        string windowStage3 = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "MainWindow.Stage3Library.cs"));

        Assert.Contains("DocumentSaveState", state, StringComparison.Ordinal);
        Assert.Contains("DocumentConcurrencyException", save, StringComparison.Ordinal);
        Assert.Contains("DocumentRevisionKind.Checkpoint", save, StringComparison.Ordinal);
        Assert.Contains("ImportDocumentAsync", import, StringComparison.Ordinal);
        Assert.Contains("Key.S", behavior, StringComparison.Ordinal);
        Assert.Contains("Key.N", behavior, StringComparison.Ordinal);
        Assert.Contains("Key.O", behavior, StringComparison.Ordinal);
        Assert.Contains("TryCheckpointBeforeCloseAsync", behavior, StringComparison.Ordinal);
        Assert.Contains("DocumentSaveState", windowStage3, StringComparison.Ordinal);
        Assert.Contains("Edits are saved locally with debounced autosave", windowStage3, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage3LibraryAndImporterUseCurrentTruthfulAndBoundedContracts()
    {
        string root = RepositoryRoot();
        string library = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.App", "Views", "DocumentLibraryPanel.axaml"));
        string docx = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Files", "BoundedDocxTextExtractor.cs"));
        string html = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Files", "BoundedHtmlTextExtractor.cs"));
        string tests = File.ReadAllText(Path.Combine(root, "tests", "CloudScribe.Infrastructure.Tests", "BoundedLocalDocumentImporterTests.cs"));

        Assert.Contains("PlaceholderText=\"Search local documents\"", library, StringComparison.Ordinal);
        Assert.DoesNotContain("Watermark=", library, StringComparison.Ordinal);
        Assert.Contains("MaxArchiveExpandedBytes", docx, StringComparison.Ordinal);
        Assert.Contains("MaxCompressionRatio", docx, StringComparison.Ordinal);
        Assert.Contains("DtdProcessing.Prohibit", docx, StringComparison.Ordinal);
        Assert.Contains("XmlResolver = null", docx, StringComparison.Ordinal);
        Assert.Contains("IsDiscardedHtmlContainer", html, StringComparison.Ordinal);
        Assert.Contains("DocxRejectsParentTraversalEntry", tests, StringComparison.Ordinal);
        Assert.Contains("DocxRejectsDtdDeclarations", tests, StringComparison.Ordinal);
        Assert.Contains("DocxRejectsSuspiciousCompressionRatio", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage3RecoveryKeepsVerifiedPreMigrationBackupAndFailClosedRestoreEvidence()
    {
        string root = RepositoryRoot();
        string initializer = File.ReadAllText(Path.Combine(root, "src", "CloudScribe.Infrastructure", "Persistence", "DatabaseInitializer.cs"));
        string tests = File.ReadAllText(Path.Combine(root, "tests", "CloudScribe.Infrastructure.Tests", "DatabaseRecoveryTests.cs"));

        Assert.Contains("BackupDatabase", initializer, StringComparison.Ordinal);
        Assert.Contains("PRAGMA integrity_check", initializer, StringComparison.Ordinal);
        Assert.Contains("RestoreBackup", initializer, StringComparison.Ordinal);
        Assert.Contains("ClearAllPools", initializer, StringComparison.Ordinal);
        Assert.Contains("FailedMigrationRestoresVerifiedPreMigrationDatabase", tests, StringComparison.Ordinal);
        Assert.Contains("CorruptDatabaseFailsClosedWithoutReplacingOriginalBytes", tests, StringComparison.Ordinal);
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
