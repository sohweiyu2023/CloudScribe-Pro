using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    private static readonly string[] SyntheticArticleParts = ["Synthetic ", "original text."];
    [Fact]
    public async Task SwitchingToSameTextDocumentDuringPreparationCannotChangeParentOrChildTitle()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        const string text = "Synthetic original text.";
        var first = await library.CreateAsync("First synthetic article", text, TestContext.Current.CancellationToken);
        var second = await library.CreateAsync("Second synthetic article", text, TestContext.Current.CancellationToken);
        var groups = new BlockingGroupStore();
        await using var autosave = new DocumentAutosaveCoordinator(library, TimeProvider.System);
        using var model = CreateViewModel();
        model.ConfigureStage3DocumentWorkflow(library, autosave, groups);
        await model.OpenDocumentCommand.ExecuteAsync(first.Id);
        Task<int> creating = model.CreateLocalNarrationPartsAsync(SyntheticArticleParts, text);
        await groups.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await model.OpenDocumentCommand.ExecuteAsync(second.Id);
        groups.Release.SetResult();
        Assert.Equal(2, await creating);
        Assert.Equal(first.Id, groups.ParentId);
        Assert.Equal(second.Id, model.CurrentDocumentId);
        Assert.Equal(second, await library.OpenAsync(second.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(groups.Members);
        foreach (var member in groups.Members)
            Assert.StartsWith("First synthetic article - Part", (await library.OpenAsync(member.DocumentId, TestContext.Current.CancellationToken))!.Title, StringComparison.Ordinal);
    }

    private sealed class BlockingGroupStore : INarrationPartGroupStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid ParentId { get; private set; }
        public IReadOnlyList<NarrationPartMember>? Members { get; private set; }
        public async Task<NarrationPartPreparation> BeginAsync(Guid parentId, Guid sourceRevisionId, string sourceSha256,
            IReadOnlyList<string> orderedPartHashes, CancellationToken cancellationToken = default)
        {
            ParentId = parentId;
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(Guid.NewGuid(), false, orderedPartHashes.Count);
        }
        public Task RegisterAsync(Guid groupId, IReadOnlyList<NarrationPartMember> parts, CancellationToken cancellationToken = default)
        {
            Members = parts.ToArray();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<NarrationLibraryArticle>> QueryLibraryAsync(string query, int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NarrationLibraryArticle>>([]);
    }
}
