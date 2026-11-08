namespace CloudScribe.Architecture.Tests;

/// <summary>Guards the UI wiring to the behaviorally tested batch operations.</summary>
public sealed class BatchBinderRegressionTests
{
    [Fact]
    public void DraftSaveFailureEscapesBeforeCallersCanClaimSuccess()
    {
        string source = BinderSource("Files");
        int start = source.IndexOf("private async Task SaveDraftAsync", StringComparison.Ordinal);
        int end = source.IndexOf("private async Task RestoreDraftAsync", start, StringComparison.Ordinal);
        string save = source[start..end];
        Assert.Contains("BatchDiagnosticOperation.SaveDraft, error);", save, StringComparison.Ordinal);
        Assert.Contains("throw;", save, StringComparison.Ordinal);
        Assert.DoesNotContain("ctx.Status.Text =", save, StringComparison.Ordinal);
        Assert.Contains("may not be saved to disk. Keep this window open", BinderSource(null), StringComparison.Ordinal);
        Assert.Contains("if (session.IsDraft) ctx.CloseWhenIdle = false;", BinderSource(null), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceDiagnosticsAreWiredAndKeyboardDeleteUsesRemoveOperation()
    {
        Assert.Contains("documentLibrary, store, catalogLogger, narrationCopies)", BinderSource(null), StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.Delete ? BatchDiagnosticOperation.Remove", BinderSource("Rows"), StringComparison.Ordinal);
    }

    [Fact]
    public void ArrangementCapturesSnapshotBeforeSavingAndRowsIncludeIdentity()
    {
        string source = BinderSource("Rows");
        Assert.Contains("var before = session.Items.ToArray();", source, StringComparison.Ordinal);
        Assert.Contains("rows.Select(x => x.Item.ItemId +", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftAndPendingImportUseDiskAwareReaders()
    {
        string source = BinderSource("Files");
        Assert.Contains("ctx.Importer.ReadDraftSourceAsync(item, session.Texts", source, StringComparison.Ordinal);
        Assert.Contains("ctx.Importer.ReadPendingAsync(item, session.Texts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (session.Texts.ContainsKey(item.ItemId)) continue;", source, StringComparison.Ordinal);
        int missing = source.IndexOf("if (missing.Count > 0)", StringComparison.Ordinal);
        int create = source.IndexOf("var batch = new NarrationBatch", missing, StringComparison.Ordinal);
        Assert.Contains("return;", source[missing..create], StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySelectionKeepsKeyboardUndoReachableAndCheckboxesHaveNames()
    {
        string source = BinderSource(null);
        int list = source.IndexOf("public ListBox List", StringComparison.Ordinal);
        int overlay = source.IndexOf("public Canvas Overlay", list, StringComparison.Ordinal);
        Assert.Contains("Focusable = true", source[list..overlay], StringComparison.Ordinal);
        Assert.Contains("if (listHadFocus) RestoreBatchListFocus(ctx);", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(ctx.Subfolders, \"Include subfolders when adding a folder\")", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(ctx.Numbering, \"Number the MP3 files in this order\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleSiblingKeepsSeparateRecoveryDecisionAndBlocksNewApproval()
    {
        string review = BinderSource("Files");
        Assert.Contains("session.RunDecision = await session.Gate.CheckRunAsync", review, StringComparison.Ordinal);
        Assert.Contains("Resume can still check selected existing jobs first", review, StringComparison.Ordinal);
        Assert.Contains("session.RunDecision = null", review, StringComparison.Ordinal);
        string controls = BinderSource(null);
        Assert.Contains("session.RunDecision is { Allowed: true }, session.Profile?.Mode", controls, StringComparison.Ordinal);
        Assert.Contains("session.Request is not null && decision is { Allowed: false }", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrationCopyPreparationGuardsCloseAndReservesSharedBatchEntry()
    {
        string source = NarrationBinderSource();
        Assert.Contains("dialog.Closing += (_, args) => args.Cancel = saving", source, StringComparison.Ordinal);
        int start = source.IndexOf("private async Task OpenNarrationCopyBatchAsync", StringComparison.Ordinal);
        string opening = source[start..];
        Assert.True(opening.IndexOf("_batchDialogOpen = true", StringComparison.Ordinal) < opening.IndexOf("await ", StringComparison.Ordinal));
        Assert.Contains("openingReserved: true", opening, StringComparison.Ordinal);
        Assert.Contains("finally { _batchDialogOpen = false; }", opening, StringComparison.Ordinal);
        Assert.Contains("if (_batchDialogOpen && !openingReserved)", BinderSource(null), StringComparison.Ordinal);
    }

    private static string NarrationBinderSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
                return File.ReadAllText(Path.Combine(directory.FullName, "src", "CloudScribe.App", "Composition", "GoogleTtsUsabilityHotfixBinder.NarrationCopy.cs"));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }

    private static string BinderSource(string? part)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
                return File.ReadAllText(Path.Combine(directory.FullName, "src", "CloudScribe.App", "Composition", "GoogleTtsUsabilityHotfixBinder.Batch" + (part is null ? string.Empty : "." + part) + ".cs"));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }
}
