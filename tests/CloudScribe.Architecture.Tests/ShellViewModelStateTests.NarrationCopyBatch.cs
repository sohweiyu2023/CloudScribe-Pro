using System.Reflection;
using System.Runtime.CompilerServices;
using CloudScribe.App.Composition;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task AwaitingOneFileCopyPreparationBlocksEveryBatchEntryAndPreservesExistingDraft()
    {
        await using var fixture = await AutosaveDiskFixture.CreateAsync();
        var library = fixture.NewLibrary();
        var original = await library.CreateAsync("Original", "Read https://example.org then continue.", TestContext.Current.CancellationToken);
        var copy = await CopyStore(fixture).PrepareAsync(original.Id, original.CurrentRevisionId!.Value,
            BatchNarrationIdentity.Hash(original.Text), TestContext.Current.CancellationToken);
        var drafts = new FileBatchDraftStore(fixture.Paths.RootDirectory);
        var existing = BatchDraft.Empty(DateTimeOffset.UtcNow) with { Name = "Keep this draft", OutputFolder = fixture.Paths.RootDirectory };
        await drafts.SaveAsync(existing, TestContext.Current.CancellationToken);
        var copies = new BlockingFindCopies(copy);
        Type type = typeof(GoogleTtsUsabilityHotfixBinder);
        object binder = RuntimeHelpers.GetUninitializedObject(type);
        SetBinderField(type, binder, "<narrationCopies>P", copies);
        SetBinderField(type, binder, "<documentLibrary>P", library);
        SetBinderField(type, binder, "<paths>P", fixture.Paths);
        using var model = CreateViewModel();
        var prepare = type.GetMethod("OpenNarrationCopyBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var preparing = Assert.IsAssignableFrom<Task>(prepare.Invoke(binder, [null, model, copy]));
        await copies.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((bool)type.GetField("_batchDialogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binder)!);
        var open = type.GetMethod("OpenBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(open.Invoke(binder, [null, model, null, false]));
        Assert.Contains("already opening or open", model.StatusMessage, StringComparison.Ordinal);
        Assert.False(preparing.IsCompleted);
        copies.Release.SetResult();
        await preparing;
        Assert.Contains("has not been replaced", model.StatusMessage, StringComparison.Ordinal);
        var retained = await drafts.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(retained); Assert.Equal(existing.Name, retained.Name);
        Assert.Equal(existing.OutputFolder, retained.OutputFolder); Assert.Empty(retained.Items);
        Assert.False((bool)type.GetField("_batchDialogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binder)!);
    }

    private static void SetBinderField(Type type, object binder, string name, object value) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(binder, value);

    private sealed class BlockingFindCopies(NarrationCopyPreparation copy) : INarrationCopyStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NarrationCopyPreparation?> FindAsync(Guid narrationDocumentId, CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return copy;
        }
        public Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentSnapshot> ImportBatchSourceAsync(Guid batchId, Guid itemId, string rawSha256, string title, string normalizedText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
