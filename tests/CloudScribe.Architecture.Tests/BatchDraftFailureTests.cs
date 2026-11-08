#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using CloudScribe.App.Composition;
using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Architecture.Tests;

public sealed class BatchDraftFailureTests
{
    [Fact]
    public async Task FailedAutosaveKeepsRemovalUndoAndFailedUndoKeepsRestoredRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-draft-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A regular file in place of the profile directory makes persistence fail deterministically.
            string blocked = Path.Combine(root, "occupied");
            await File.WriteAllTextAsync(blocked, "synthetic", TestContext.Current.CancellationToken);
            var binderType = typeof(GoogleTtsUsabilityHotfixBinder);
            var sessionType = binderType.GetNestedType("BatchSession", BindingFlags.NonPublic)!;
            var contextType = binderType.GetNestedType("BatchContext", BindingFlags.NonPublic)!;
            object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            sessionType.GetProperty("Drafts")!.SetValue(session, new FileBatchDraftStore(blocked));
            sessionType.GetProperty("OutputFolder")!.SetValue(session, root);
            var draft = (List<BatchItem>)sessionType.GetProperty("Draft")!.GetValue(session)!;
            var undo = (IList)sessionType.GetProperty("Undo")!.GetValue(session)!;
            var items = BatchPlanner.Plan([new BatchSourceFile("synthetic.txt", "hash", 4, "text")], title => title + ".mp3", _ => false);
            draft.AddRange(items);
            // These failure paths do not touch controls or other app services. Avoid initializing any real profile/UI.
            object ctx = RuntimeHelpers.GetUninitializedObject(contextType);
            contextType.GetProperty("Session")!.SetValue(ctx, session);
            object binder = RuntimeHelpers.GetUninitializedObject(binderType);
            Func<IReadOnlyList<BatchItem>, IReadOnlyCollection<Guid>, IReadOnlyList<BatchItem>> remove =
                (rows, ids) => BatchEditor.Remove(rows, ids, draft: true);
            var edit = (Task)binderType.GetMethod("EditBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(binder, [ctx, "Removed", remove, new[] { items[0].ItemId }])!;
            await Assert.ThrowsAnyAsync<IOException>(() => edit);
            Assert.Empty(draft);
            Assert.Single(undo.Cast<object>());
            var restore = (Task)binderType.GetMethod("UndoBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(binder, [ctx])!;
            await Assert.ThrowsAnyAsync<IOException>(() => restore);
            Assert.Equal(items[0].ItemId, Assert.Single(draft).ItemId);
            Assert.Empty(undo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
