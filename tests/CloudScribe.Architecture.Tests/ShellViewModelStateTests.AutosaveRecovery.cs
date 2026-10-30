using System.ComponentModel;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyntheticPastedAndEditedDraftSurvivesDebounceOrImmediateCloseAndReopen(bool waitForDebounce)
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        const string pasted = "Synthetic pasted draft: \u4E2D\u6587 \U0001F600 e\u0301\nsecond line";
        string expected = pasted + "\nedited before close";
        Guid documentId;
        await using (var autosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System))
        {
            using ShellViewModel model = CreateViewModel();
            model.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), autosave);
            model.ConfigureSavedVoiceSetups(new StudioPreferencesStore(fixture.Paths));
            await model.NewDocumentCommand.ExecuteAsync(null);
            documentId = Assert.IsType<Guid>(model.CurrentDocumentId);
            model.DocumentTitle = "Synthetic recovery draft";
            model.DocumentText = pasted;
            model.DocumentText = expected;
            Assert.True(model.IsDocumentDirty);
            if (waitForDebounce)
            {
                await WaitForSyntheticDraftStateAsync(model, () => !model.IsDocumentDirty && !model.IsDocumentSaving);
                Assert.Equal(expected, (await fixture.NewLibrary().OpenAsync(documentId, TestContext.Current.CancellationToken))!.Text);
            }
            Assert.True(await model.PrepareDocumentCloseAsync());
            Assert.False(model.IsDocumentDirty);
            Assert.Null(model.PreparedGoogleGenerationSpendReview);
            Assert.False(model.GenerateWithGoogleCommand.IsRunning);
        }

        SqliteConnection.ClearAllPools();
        var freshPreferences = new StudioPreferencesStore(fixture.Paths);
        Assert.Equal(documentId, freshPreferences.State.LastDocumentId);
        await using var restartedAutosave = new DocumentAutosaveCoordinator(fixture.NewLibrary(), TimeProvider.System);
        using ShellViewModel restarted = CreateViewModel();
        restarted.ConfigureStage3DocumentWorkflow(fixture.NewLibrary(), restartedAutosave);
        restarted.ConfigureSavedVoiceSetups(freshPreferences);
        restarted.StartDocumentWorkspace();
        await WaitForSyntheticDraftStateAsync(restarted, () => restarted.CurrentDocumentId == documentId && !restarted.IsDocumentSaving);
        Assert.Equal("Synthetic recovery draft", restarted.DocumentTitle);
        Assert.Equal(expected, restarted.DocumentText);
        Assert.False(restarted.IsDocumentDirty);
        restarted.DocumentText += "\nedit after reopening";
        Assert.True(await restarted.PrepareDocumentCloseAsync());
        Assert.Equal(expected + "\nedit after reopening", (await fixture.NewLibrary().OpenAsync(documentId, TestContext.Current.CancellationToken))!.Text);
    }

    private static async Task WaitForSyntheticDraftStateAsync(ShellViewModel model, Func<bool> ready)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs args) { if (ready()) changed.TrySetResult(); }
        model.PropertyChanged += OnChanged;
        try
        {
            if (ready()) return;
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally { model.PropertyChanged -= OnChanged; }
    }

    private sealed class AutosaveDiskFixture(string root, AppPaths paths, DbContextOptions<CloudScribeDbContext> options) : IAsyncDisposable
    {
        public AppPaths Paths { get; } = paths;
        public EfDocumentLibrary NewLibrary() => new(new AutosaveContextFactory(options), new DocumentContentStore(Paths), TimeProvider.System);
        public static async Task<AutosaveDiskFixture> CreateAsync(string? migration = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-synthetic-autosave", Guid.NewGuid().ToString("N"));
            AppPaths paths = new(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root }));
            paths.EnsureDirectories();
            var options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, ForeignKeys = true }.ConnectionString).Options;
            var context = new CloudScribeDbContext(options);
            await using (context.ConfigureAwait(false))
                await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context)
                    .MigrateAsync(migration, TestContext.Current.CancellationToken).ConfigureAwait(false);
            return new AutosaveDiskFixture(root, paths, options);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-synthetic-autosave")) + Path.DirectorySeparatorChar;
            string actual = Path.GetFullPath(root);
            if (!actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(actual), "N", out _))
                throw new InvalidOperationException("Synthetic fixture cleanup target is outside its test root.");
            if (Directory.EnumerateFileSystemEntries(actual, "*", SearchOption.AllDirectories)
                .Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidOperationException("Synthetic fixture contains a linked entry; cleanup stopped.");
            Directory.Delete(actual, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AutosaveContextFactory(DbContextOptions<CloudScribeDbContext> options) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(options);
    }
}

