using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static async Task<bool> PreviewNarrationCopyAsync(Window owner, ShellViewModel model)
    {
        string original = model.DocumentText;
        var result = NarrationUrlOmission.Transform(original);
        var dialog = CreateArticleExportDialog();
        dialog.Title = "Preview narration without web addresses";
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        var status = NewDescription(result.OmittedUrlCount == 0
            ? "No web addresses were found. The text and its existing review path stay unchanged."
            : $"{result.OmittedUrlCount} web address(es) will be skipped. Your original stays unchanged. Save and open a separate narration copy before reviewing its cost.");
        if (!result.HasSpeakableContent) status.Text += " No spoken words remain, so no narration request can be made.";
        body.Children.Add(status);
        body.Children.Add(NarrationPreviewText(result.OutputText));
        var save = NewButton("Save and use narration copy");
        save.IsEnabled = result.OmittedUrlCount > 0 && result.HasSpeakableContent;
        body.Children.Add(save);
        var close = NewButton("Close preview"); close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        bool opened = false, saving = false;
        dialog.Closing += (_, args) => args.Cancel = saving;
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false; close.IsEnabled = false; saving = true;
            try
            {
                var copy = await model.UseNarrationCopyAsync(original).ConfigureAwait(true);
                opened = copy.Narration.Id != copy.SourceDocumentId;
                saving = false; dialog.Close();
            }
            catch (Exception error) when (BatchDiagnosticsNonFatal(error))
            { status.Text = "The narration copy could not be prepared: " + error.Message + " Your original is retained; no audio was requested. If an earlier copy was edited, save the original text as a new document, then preview URL skipping again."; }
            finally { saving = false; close.IsEnabled = true; }
        };
        dialog.Content = body;
        DialogBounds.Fit(dialog, owner);
        await ShowNarrationPreviewAsync(dialog, owner).ConfigureAwait(true);
        return opened;
    }

    private static async Task ShowNarrationPreviewAsync(Window dialog, Window owner)
    {
        var invoker = owner.FocusManager?.GetFocusedElement() as Control;
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            dialog.Close(); // Closing retains the narration-copy save guard.
        };
        try { await dialog.ShowDialog(owner).ConfigureAwait(true); }
        finally { RestoreDialogInvoker(owner, invoker); }
    }

    private static bool BatchDiagnosticsNonFatal(Exception error) =>
        error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static TextBox NarrationPreviewText(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Height = 280,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
    };

    private static string BatchUrlPolicyText(string? version) => version switch
    {
        NarrationUrlOmission.LegacyVersion => "Legacy URL policy: only HTTP(S) and www addresses are skipped. Bare addresses such as 84000.co remain. Prepare a new copy or draft from the original text to use bare-domain skipping.",
        NarrationUrlOmission.CurrentVersion => "Bare-domain skipping is enabled for the supported suffixes, including 84000.co. Check the narration preview before approval.",
        _ => "Unsupported URL policy. This saved narration cannot be prepared or upgraded automatically.",
    };

    private async Task PreviewBatchNarrationAsync(BatchContext ctx)
    {
        if (SelectedBatchRows(ctx) is not [var item]) return;
        string text;
        int count;
        bool skip = ctx.Session.Batch?.Settings.SkipUrls ?? ctx.Session.SkipUrls;
        string? version = ctx.Session.Batch is { } saved ? saved.Settings.UrlTransformVersion : ctx.Session.ToDraft().ResolveUrlTransformVersion();
        if (item.DocumentId is { } documentId)
        {
            var document = await documentLibrary!.OpenAsync(documentId).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The saved narration text is unavailable.");
            if (document.CurrentRevisionId != item.RevisionId) throw new InvalidOperationException("The narration text changed. Prepare and review it again.");
            text = document.Text; count = item.NarrationSource?.OmittedUrlCount ?? 0;
            if (skip && item.Exclusion == BatchExclusion.NoSpeakableText)
            {
                var omitted = NarrationUrlOmission.Transform(text, version);
                text = omitted.OutputText; count = omitted.OmittedUrlCount;
            }
        }
        else
        {
            var source = await ctx.Importer.ReadDraftSourceAsync(item, ctx.Session.Texts, BatchMaximumSourceBytes).ConfigureAwait(true);
            if (source.Text is null) throw new InvalidOperationException("The original file cannot be read for preview.");
            var result = skip ? NarrationUrlOmission.Transform(source.Text, version) : null;
            text = result?.OutputText ?? source.Text; count = result?.OmittedUrlCount ?? 0;
        }
        var dialog = CreateArticleExportDialog(); dialog.Title = "Narration text preview";
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        body.Children.Add(NewDescription($"{item.SourceFileName}: {count} URL(s) skipped. " +
            (skip ? "Original text and files are kept unchanged. Costs use this narration text after saving." : "URL skipping is off; this is the original text.")));
        if (skip) body.Children.Add(NewDescription(BatchUrlPolicyText(version)));
        body.Children.Add(NarrationPreviewText(text));
        if (skip && !text.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit))
            body.Children.Add(NewDescription("No spoken words remain. This file is excluded locally; no request is sent."));
        var close = NewButton("Close preview"); close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        dialog.Content = body; DialogBounds.Fit(dialog, ctx.Dialog);
        await ShowNarrationPreviewAsync(dialog, ctx.Dialog).ConfigureAwait(true);
    }

    private async Task AddSavedNarrationCopyNoticeAsync(Window owner, StackPanel body, ShellViewModel model)
    {
        if (narrationCopies is null || model.CurrentDocumentId is not Guid id) return;
        NarrationCopyPreparation? copy;
        try { copy = await narrationCopies.FindAsync(id).ConfigureAwait(true); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { body.Children.Add(NewDescription("This narration copy or its original changed. Keep existing audio. To prepare another copy, put the original text in a new document, save it, then preview URL skipping again.")); return; }
        if (copy is null) return;
        body.Children.Add(NewDescription($"Narration copy: {copy.OmittedUrlCount} web address(es) skipped. Original unchanged in Library. Any cost review below uses this saved copy. The checkbox prepares a copy; turning it off does not restore links in the open copy."));
        body.Children.Add(NewDescription(BatchUrlPolicyText(copy.TransformVersion)));
        var original = NewButton("Open original in Studio");
        original.Click += async (_, _) =>
        {
            await model.OpenDocumentCommand.ExecuteAsync(copy.SourceDocumentId).ConfigureAwait(true);
            if (model.CurrentDocumentId == copy.SourceDocumentId) owner.Close();
        };
        body.Children.Add(original);
        if (System.Text.Encoding.UTF8.GetByteCount(copy.Narration.Text) < 4_000) return;
        body.Children.Add(NewDescription("This long copy needs its own approval. Review it as a one-file batch using the existing Long Audio setup; the original's approval is not reused."));
        var batch = NewButton("Review this copy as a one-file batch");
        AutomationProperties.SetName(batch, "Review this narration copy as a one-file batch");
        batch.Click += async (_, _) =>
        {
            batch.IsEnabled = false;
            try { await OpenNarrationCopyBatchAsync(owner, model, copy).ConfigureAwait(true); }
            catch (Exception error) when (BatchDiagnosticsNonFatal(error)) { model.StatusMessage = "Could not open the copy's batch: " + error.Message; }
            finally { batch.IsEnabled = true; }
        };
        body.Children.Add(batch);
    }

    private async Task OpenNarrationCopyBatchAsync(Window owner, ShellViewModel model, NarrationCopyPreparation copy)
    {
        if (narrationCopies is null || documentLibrary is null) throw new InvalidOperationException("Narration copies are unavailable.");
        if (_batchDialogOpen) throw new InvalidOperationException("A batch window is already open. Close it before starting this one-file review.");
        _batchDialogOpen = true; // Reserve every batch entrypoint before the first await or draft write.
        try
        {
            copy = await narrationCopies.FindAsync(copy.Narration.Id).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The narration copy is no longer available.");
            var drafts = new FileBatchDraftStore(paths.RootDirectory, catalogLogger);
            if (await drafts.LoadAsync().ConfigureAwait(true) is { } existing && (existing.HasFiles || existing.OutputFolder is not null))
            {
                model.StatusMessage = "An unsaved batch draft already exists. Open Batch MP3 to finish or discard that draft first; it has not been replaced.";
                return;
            }
            string title = copy.Narration.Title;
            string outputName = AudioOutputPresentation.SuggestedFileName(title, ".mp3");
            string sourceName = Path.GetFileNameWithoutExtension(outputName) + ".txt";
            var item = new BatchItem(Guid.NewGuid(), 1, sourceName, copy.SourceTextSha256,
                0, BatchPlanner.CountCharacters(copy.Narration.Text), outputName, BatchItemState.Ready,
                DocumentId: copy.Narration.Id, RevisionId: copy.Narration.CurrentRevisionId,
                NarrationTextSha256: BatchNarrationIdentity.Hash(copy.Narration.Text),
                NarrationSource: new(copy.SourceDocumentId, copy.SourceRevisionId, copy.SourceTextSha256, copy.TransformVersion, copy.OmittedUrlCount));
            // This is an existing saved library document, not a fabricated TXT import. The draft keeps its real identity.
            await drafts.SaveAsync(new(BatchDraft.CurrentVersion, title, null, false, BatchEngines.LongAudioFirst,
                BatchFailurePolicy.SkipAndContinue, [item], DateTimeOffset.UtcNow, SkipUrls: true, UrlTransformVersion: copy.TransformVersion)).ConfigureAwait(true);
            await OpenBatchAsync(owner, model, openingReserved: true).ConfigureAwait(true);
        }
        finally { _batchDialogOpen = false; }
    }
}
