using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    /// <summary>Files and folders dropped from Explorer are added like picked ones, after the current list.</summary>
    private void WireBatchDrop(BatchContext ctx)
    {
        DragDrop.SetAllowDrop(ctx.DropZone, true);
        DragDrop.AddDragEnterHandler(ctx.DropZone, (_, e) => UpdateDrop(e));
        DragDrop.AddDragOverHandler(ctx.DropZone, (_, e) => UpdateDrop(e));
        DragDrop.AddDragLeaveHandler(ctx.DropZone, (_, _) => SetDropHighlight(ctx, false));
        DragDrop.AddDropHandler(ctx.DropZone, (_, e) =>
        {
            SetDropHighlight(ctx, false);
            // Read the dropped items now; the drag data is only valid during this event.
            var items = e.DataTransfer.TryGetFiles();
            e.Handled = true;
            if (items is null || items.Length == 0)
            {
                ctx.Status.Text = "Only files and folders can be dropped here. Drag TXT files from File Explorer.";
                return;
            }
            _ = GuardBatchAsync(ctx, BatchDiagnosticOperation.Drop, () => AddStorageItemsAsync(ctx, items, null));
        });

        void UpdateDrop(DragEventArgs e)
        {
            bool files = e.DataTransfer.Contains(DataFormat.File);
            bool allowed = files && !ctx.Session.Running && !ctx.Session.Busy && ctx.AddFiles.IsEnabled;
            e.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropHighlight(ctx, allowed);
            if (files && !allowed)
                ctx.Status.Text = ctx.Session.Running ? "Files cannot be added while the batch is running." : "Files cannot be added to this batch right now.";
            e.Handled = true;
        }
    }

    /// <summary>Expands folders (optionally with subfolders) to their TXT files, sorts them naturally by folder and name,
    /// skips anything already listed and adds the rest after the current files. One TXT always makes one MP3.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One linear add: expand, read with progress, plan, save.")]
    private async Task AddStorageItemsAsync(BatchContext ctx, IStorageItem[] picked, string? folderName)
    {
        var session = ctx.Session;
        var token = BeginBatchWork(ctx);
        bool recursive = ctx.Subfolders.IsChecked == true;
        var files = new List<(IStorageFile File, string SortKey)>();
        int ignored = 0, foldersWithout = 0;
        foreach (var entry in picked)
        {
            token.ThrowIfCancellationRequested();
            if (entry is IStorageFile file)
            {
                if (file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) files.Add((file, file.TryGetLocalPath() ?? file.Name));
                else ignored++;
            }
            else if (entry is IStorageFolder folder)
            {
                int found = files.Count;
                await CollectTextFilesAsync(folder, folder.TryGetLocalPath() ?? folder.Name, recursive, files, token).ConfigureAwait(true);
                if (files.Count == found) foldersWithout++;
                session.OutputFolder ??= session.IsDraft && session.Draft.Count == 0 ? folder.TryGetLocalPath() : null;
                folderName ??= picked.Length == 1 ? folder.Name : null;
            }
        }
        string notes = (ignored > 0 ? $" {ignored} item(s) that are not .txt files were left out." : string.Empty)
            + (foldersWithout > 0 ? $" {foldersWithout} folder(s) had no .txt files{(recursive ? string.Empty : " (tick Include subfolders to look inside subfolders)")}." : string.Empty);
        // The same TXT added twice is still one MP3; two different files with the same words stay two tracks.
        var already = files.Where(x => BatchEditor.IsAlreadyListed(session.Items, x.File.TryGetLocalPath())).ToArray();
        files = files.Except(already).GroupBy(x => x.File.TryGetLocalPath() ?? Guid.NewGuid().ToString(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (already.Length > 0) notes += $" {already.Length} file(s) were already in the list and were not added twice.";
        if (files.Count == 0) { ctx.Status.Text = "No new TXT files to add." + notes; return; }
        files.Sort((a, b) => BatchNaturalOrder.Instance.Compare(a.SortKey, b.SortKey));

        var read = new List<BatchSourceFile>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ReportBatchWork(ctx, "Reading files", i, files.Count);
            var file = files[i].File;
            string? local = file.TryGetLocalPath();
            try
            {
                var stream = await file.OpenReadAsync().ConfigureAwait(true);
                await using (stream.ConfigureAwait(true))
                    read.Add(await ctx.Importer.ReadAsync(file.Name, stream, BatchMaximumSourceBytes, token).ConfigureAwait(true) with { SourcePath = local });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                BatchDiagnostics.Failure(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.AddFiles, error);
                read.Add(new(file.Name, string.Empty, 0, null, BatchExclusion.Unreadable, local));
            }
            if (session.IsDraft && session.OutputFolder is null && local is not null) session.OutputFolder = System.IO.Path.GetDirectoryName(local);
        }
        ReportBatchWork(ctx, "Reading files", files.Count, files.Count);
        var snapshot = session.Items.ToArray();
        var before = snapshot.Select(x => x.ItemId).ToHashSet();
        var planned = BatchPlanner.Append(session.Items, read, t => t + ".mp3", n => BatchOutputExists(session, n), session.Numbered, keepGivenOrder: true);
        var added = planned.Where(x => !before.Contains(x.ItemId)).ToArray();
        var textByFile = read.Where(x => x.Text is not null).GroupBy(x => (x.FileName, x.Sha256, x.SourcePath)).ToDictionary(g => g.Key, g => g.First().Text!);
        foreach (var item in added.Where(x => x.IsIncluded))
            if (textByFile.TryGetValue((item.SourceFileName, item.SourceSha256, item.SourcePath), out string? text)) session.Texts[item.ItemId] = text;
        int unusable = added.Count(x => !x.IsIncluded);
        string summary = $"Added {added.Length} file(s) after the current list" + (unusable > 0 ? $"; {unusable} cannot be used (each row says why)." : ".");

        if (session.Batch is { } batch)
        {
            // A saved batch that has not finished takes new files straight into the library; its approval then covers
            // the old files only, so the batch is reviewed again before anything new can start.
            var items = planned.Select(x => before.Contains(x.ItemId) ? x : x with { EstimatedMaximumMinorUnits = session.Profile?.EstimateMinorUnits(x.Characters) ?? 0 }).ToArray();
            batch = await session.Store.SaveAsync(batch with { Items = items, State = batch.State == BatchQueueState.Finished ? BatchQueueState.Paused : batch.State, FinishedUtc = null }).ConfigureAwait(true);
            session.Batch = batch;
            session.Batch = await ImportPendingAsync(ctx, batch, token).ConfigureAwait(true);
            model(ctx).RefreshDocumentLibraryAfterBatchImport();
            await ReviewBatchAsync(ctx).ConfigureAwait(true);
            ctx.Status.Text = summary + " They are in the library and need approval before they can start." + RenamedText(session) + notes;
        }
        else
        {
            session.Name ??= folderName ?? $"Batch {DateTime.Now:d MMM yyyy HH:mm}";
            PushBatchUndo(session, snapshot, "Add");
            ReplaceDraft(session, planned);
            await SaveDraftAsync(ctx).ConfigureAwait(true);
            ctx.Status.Text = summary + RenamedText(session) + notes + " The draft is saved automatically; check the order, voice and output folder, then choose Add to library and save batch.";
        }
        SelectBatchItems(ctx, added.Select(x => x.ItemId).ToArray());

        static ShellViewModel model(BatchContext c) => c.Model;
    }

    private static async Task CollectTextFilesAsync(IStorageFolder folder, string key, bool recursive, List<(IStorageFile, string)> files, CancellationToken token)
    {
        await foreach (var entry in folder.GetItemsAsync().WithCancellation(token).ConfigureAwait(true))
        {
            if (entry is IStorageFile file && file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                files.Add((file, file.TryGetLocalPath() ?? key + "/" + file.Name));
            else if (recursive && entry is IStorageFolder child)
                await CollectTextFilesAsync(child, child.TryGetLocalPath() ?? key + "/" + child.Name, recursive, files, token).ConfigureAwait(true);
        }
    }

    private async Task ChooseBatchOutputAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        if (session.AnyStarted) { ctx.Status.Text = "The output folder cannot change after a file has started."; return; }
        var folders = await ctx.Dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose where to save the MP3 files", AllowMultiple = false }).ConfigureAwait(true);
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        if (session.Batch is { } batch)
        {
            var renamed = BatchPlanner.AssignOutputNames(batch.Items, t => t + ".mp3", n => File.Exists(System.IO.Path.Combine(path, n)), batch.NumberTracks);
            session.Batch = await session.Store.SaveAsync(batch with { OutputFolder = path, Items = renamed }).ConfigureAwait(true);
        }
        else
        {
            session.OutputFolder = path;
            ReplaceDraft(session, BatchPlanner.AssignOutputNames(session.Draft, t => t + ".mp3", n => BatchOutputExists(session, n), session.NumberTracks));
            await SaveDraftAsync(ctx).ConfigureAwait(true);
        }
        ctx.Status.Text = $"Output folder: {path}." + RenamedText(session);
    }

    private async Task SaveDraftAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        try
        {
            if (session.Draft.Count == 0 && session.OutputFolder is null) await session.Drafts.DeleteAsync().ConfigureAwait(true);
            else await session.Drafts.SaveAsync(session.ToDraft()).ConfigureAwait(true);
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            // Keep the in-memory arrangement, but fail the action so callers cannot overwrite the warning with success.
            BatchDiagnostics.Failure(catalogLogger, null, null, BatchDiagnosticOperation.SaveDraft, error);
            throw;
        }
    }

    private async Task RestoreDraftAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        var draft = await session.Drafts.LoadAsync().ConfigureAwait(true);
        if (draft is null || !draft.HasFiles && draft.OutputFolder is null) return;
        ApplyDraftSettings(session, draft);
        // Names and same-text notes are recomputed for the folder as it is now (files may have appeared meanwhile).
        ReplaceDraft(session, BatchPlanner.AssignOutputNames(session.Draft, t => t + ".mp3", n => BatchOutputExists(session, n), session.NumberTracks));
        BatchDiagnostics.Action(catalogLogger, null, null, BatchDiagnosticOperation.LoadDraft, BatchDiagnosticPhase.Returned);
        ctx.Status.Text = $"Your unsaved draft is back ({draft.Items.Count} file(s), last changed {draft.UpdatedUtc.ToLocalTime():d MMM HH:mm}). Files are read again when you save it; changed text is refreshed before review.";
    }

    private static void ApplyDraftSettings(BatchSession session, BatchDraft draft)
    {
        session.Name = draft.Name;
        session.OutputFolder = draft.OutputFolder;
        session.NumberTracks = draft.NumberTracks;
        session.Policy = draft.FailurePolicy;
        session.EngineChoice = Enumerable.Range(0, BatchDialogPresentation.EngineChoices.Count)
            .FirstOrDefault(i => string.Equals(BatchDialogPresentation.EngineForChoice(i), draft.Engine, StringComparison.Ordinal));
        ReplaceDraft(session, draft.Items);
    }

    /// <summary>Shows the unsaved draft (which is always on disk), leaving any saved batch saved.</summary>
    private static async Task ShowDraftAsync(BatchContext ctx, string message)
    {
        var session = ctx.Session;
        ClearBatchView(session);
        var draft = await session.Drafts.LoadAsync().ConfigureAwait(true);
        if (draft is not null) ApplyDraftSettings(session, draft);
        await LoadSavedListAsync(ctx).ConfigureAwait(true);
        ctx.Status.Text = message + (session.Draft.Count > 0 ? $" The draft has {session.Draft.Count} file(s)." : " Drag in TXT files or folders, or choose Add TXT files….");
    }

    private static void ClearBatchView(BatchSession session)
    {
        session.Batch = null; session.Request = null; session.Decision = null; session.ApprovedMinorUnits = null; session.ReviewText = string.Empty;
        session.RehearsalText = string.Empty;
        session.Undo.Clear();
    }

    private async Task DiscardDraftAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        if (!session.IsDraft) return;
        session.Undo.Add(new BatchUndo(session.Draft.ToArray(), "Discard", session.ToDraft()));
        ReplaceDraft(session, []);
        session.OutputFolder = null; session.Name = null; session.NumberTracks = false;
        await SaveDraftAsync(ctx).ConfigureAwait(true);
        await LoadSavedListAsync(ctx).ConfigureAwait(true);
        ctx.Status.Text = "Draft discarded. Choose Undo discard (Ctrl+Z) to bring it back while this window is open.";
    }

    private static async Task LoadSavedListAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        var batches = await session.Store.ListAsync().ConfigureAwait(true);
        var entries = new List<ComboBoxItem>();
        if (session.Draft.Count > 0 || !session.IsDraft)
            entries.Add(new ComboBoxItem
            {
                Content = session.IsDraft || session.Draft.Count > 0 ? $"Unsaved draft — {session.Draft.Count} file(s) (kept automatically)" : "New unsaved draft",
                Tag = Guid.Empty,
            });
        entries.AddRange(batches.Select(x => new ComboBoxItem
        {
            Content = $"{x.Name} — {BatchDialogPresentation.QueueState(x.State)}, {x.CompletedCount}/{x.IncludedCount} done, created {x.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}",
            Tag = x.BatchId,
        }));
        ctx.Refreshing = true;
        try
        {
            ctx.Saved.ItemsSource = entries;
            Guid current = session.Batch?.BatchId ?? Guid.Empty;
            ctx.Saved.SelectedItem = entries.FirstOrDefault(x => x.Tag is Guid id && id == current && (current != Guid.Empty || session.Draft.Count > 0));
        }
        finally { ctx.Refreshing = false; }
    }

    private async Task OpenSavedBatchAsync(BatchContext ctx, Guid id)
    {
        var session = ctx.Session;
        ClearBatchView(session);
        session.Batch = await session.Store.LoadAsync(id).ConfigureAwait(true);
        if (session.Batch is not { } batch) { ctx.Status.Text = "That batch could not be read."; return; }
        // The draft stays on disk and in the list; opening a saved batch never discards it.
        var draft = await session.Drafts.LoadAsync().ConfigureAwait(true);
        if (draft is not null) ReplaceDraft(session, draft.Items); else ReplaceDraft(session, []);
        ctx.Status.Text = $"Opened {batch.Name}.";
        if (batch.Items.Any(BatchDocumentImporter.NeedsImport))
        {
            var token = BeginBatchWork(ctx);
            session.Batch = await ImportPendingAsync(ctx, batch, token).ConfigureAwait(true);
            ctx.Model.RefreshDocumentLibraryAfterBatchImport();
        }
        await LoadSavedListAsync(ctx).ConfigureAwait(true);
        await ReviewBatchAsync(ctx).ConfigureAwait(true);
        if (ctx.Model.TryCaptureBatchVoice(out _) is not { } voice || !string.Equals(voice.VoiceStableId, batch.Settings.VoiceStableId, StringComparison.Ordinal))
            ctx.Status.Text += $" This batch uses {batch.Settings.VoiceLabel}; choose Use this batch's voice to review or resume it.";
    }

    /// <summary>Adds files that are not in the library yet: from memory, or read again from where they were added (the
    /// SHA-256 must match). An interrupted or cancelled add resumes here without making a second copy.</summary>
    private static async Task<NarrationBatch> ImportPendingAsync(BatchContext ctx, NarrationBatch batch, CancellationToken token)
    {
        var session = ctx.Session;
        var progress = new Progress<(int Done, int Total)>(p => ReportBatchWork(ctx, "Adding files to the library", p.Done, p.Total));
        var result = await ctx.Importer.ImportAsync(batch, (item, t) => ctx.Importer.ReadPendingAsync(item, session.Texts, BatchMaximumSourceBytes, t),
            progress, token).ConfigureAwait(true);
        foreach (var item in result.Items.Where(x => x.DocumentId is not null)) session.Texts.Remove(item.ItemId);
        int failed = result.Items.Count(x => x.State == BatchItemState.Failed && x.DocumentId is null && x.JobId is null);
        if (failed > 0) ctx.Status.Text = $"{failed} file(s) could not be added (each row says why). Remove them, or add them again.";
        return result;
    }

    /// <summary>Turns the draft into a saved batch. Every file is read again first: a missing file stops the save with
    /// its name, and a file that changed since it was listed is refreshed (nothing has been reviewed yet).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One linear save: check files, create, import, review.")]
    private async Task SaveDraftAsBatchAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        var model = ctx.Model;
        if (model.TryCaptureBatchVoice(out string? problem) is not { } voice) { ctx.Status.Text = problem ?? "Choose the voice first."; return; }
        if (session.OutputFolder is null) { ctx.Status.Text = "Choose the output folder first."; return; }
        var token = BeginBatchWork(ctx);
        var included = session.Draft.Where(x => x.IsIncluded).ToArray();
        var missing = new List<string>();
        int changed = 0;
        for (int i = 0; i < included.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            ReportBatchWork(ctx, "Checking files", i, included.Length);
            var item = included[i];
            var fresh = await ctx.Importer.ReadDraftSourceAsync(item, session.Texts, BatchMaximumSourceBytes, token).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(fresh.Text))
            {
                session.Texts[item.ItemId] = fresh.Text;
                int index = session.Draft.FindIndex(x => x.ItemId == item.ItemId);
                session.Draft[index] = item with { SourceSha256 = fresh.Sha256, SourceBytes = fresh.Bytes, Characters = BatchPlanner.CountCharacters(fresh.Text) };
                if (!string.Equals(item.SourceSha256, fresh.Sha256, StringComparison.Ordinal)) changed++;
                continue;
            }
            session.Texts.Remove(item.ItemId);
            missing.Add(item.SourceFileName);
        }
        if (missing.Count > 0)
        {
            await SaveDraftAsync(ctx).ConfigureAwait(true);
            ctx.Status.Text = $"{missing.Count} file(s) could not be read again: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : string.Empty)}. They may have been moved, renamed or deleted. Remove them or add them again, then save. Nothing was saved yet.";
            return;
        }
        var settings = ShellViewModel.BatchSettingsFor(voice, session.Policy) with { Engine = BatchDialogPresentation.EngineForChoice(session.EngineChoice) };
        ReplaceDraft(session, BatchPlanner.AssignOutputNames(session.Draft, t => t + ".mp3", n => BatchOutputExists(session, n), session.NumberTracks));
        var batch = new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), session.Name ?? "Batch", 0, BatchQueueState.Ready, settings, settings.Sha256(),
            session.OutputFolder, session.Profile?.Currency ?? "USD", session.Profile?.Scale ?? 2,
            session.Draft.Select(x => x with { EstimatedMaximumMinorUnits = session.Profile?.EstimateMinorUnits(x.Characters) ?? 0 }).ToArray(),
            DateTimeOffset.UtcNow, NumberTracks: session.NumberTracks);
        batch = await session.Store.SaveAsync(batch).ConfigureAwait(true);
        session.Batch = batch;
        // From here the saved batch is the record: the draft is removed, and a cancel or crash resumes on reopening.
        ReplaceDraft(session, []);
        session.OutputFolder = null; session.Name = null; session.Undo.Clear();
        await session.Drafts.DeleteAsync().ConfigureAwait(true);
        session.Batch = await ImportPendingAsync(ctx, batch, token).ConfigureAwait(true);
        session.Texts.Clear();
        if (catalogLogger is not null) CloudScribeLog.Observe(() =>
            CloudScribeLog.BatchCreated(catalogLogger, session.Batch.BatchId, session.Batch.IncludedCount, session.Batch.Items.Count - session.Batch.IncludedCount));
        model.RefreshDocumentLibraryAfterBatchImport();
        await LoadSavedListAsync(ctx).ConfigureAwait(true);
        await ReviewBatchAsync(ctx).ConfigureAwait(true);
        ctx.Status.Text = $"Saved. {session.Batch.Items.Count(x => x.DocumentId is not null)} documents were added to the library."
            + (changed > 0 ? $" {changed} file(s) had changed since they were listed; their current text was used." : string.Empty)
            + " The batch survives closing the app. " + BatchGuide.For(GuideInput(ctx)).Next;
    }

    private async Task ReviewBatchAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        session.Request = null; session.Decision = null; session.ReviewText = string.Empty; session.ApprovedMinorUnits = null;
        session.RehearsalText = string.Empty; // A rehearsal describes the batch as it was; any change needs a new one.
        if (session.Batch is not { } batch) return;
        var consent = await session.Gate.LoadConsentAsync(batch.BatchId).ConfigureAwait(true);
        session.ApprovedMinorUnits = consent?.TotalMaximumMinorUnits;
        if (batch.Items.Any(BatchDocumentImporter.NeedsImport)) { session.ReviewText = "Some files are not in the library yet; the cost review waits for them."; return; }
        if (ctx.Model.TryCaptureBatchVoice(out string? voiceProblem) is not { } voice)
        { session.ReviewText = (voiceProblem ?? "Select the batch voice") + " The review of costs and approval needs it."; return; }
        if (!string.Equals(voice.VoiceStableId, batch.Settings.VoiceStableId, StringComparison.Ordinal))
        { session.ReviewText = $"Select this batch's voice ({batch.Settings.VoiceLabel}) to review costs and approval."; return; }
        session.Decision = await session.Gate.CheckAsync(batch, voice.AccountStableId, voice.ProjectStableId).ConfigureAwait(true);
        if (session.Profile is not { } profile || session.Decision.Allowed || batch.State == BatchQueueState.Finished) return;
        ctx.Status.Text = "Reviewing each file's engine and maximum (no request is sent)…";
        var request = await BatchRequestBuilder(voice).BuildAsync(batch, profile, voice.AccountStableId, voice.ProjectStableId, consent).ConfigureAwait(true);
        session.Request = request;
        session.Batch = await SaveReviewedEnginesAsync(session.Store, batch, request).ConfigureAwait(true);
        int la = request.Items.Count(x => x.LongAudio is not null), ordinary = request.Items.Count(x => string.Equals(x.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal));
        session.ReviewText = BatchDialogPresentation.ReviewSummary(la, ordinary, request.NeedingArticleAdmission.Count(), request.Unnarratable.Count(),
            request.TotalEstimatedMaximumMinorUnits, request.LongAudioMaximumUsdCents, request.LongAudioHeadroomUsdCents, profile);
        ctx.Status.Text = "Review ready. Check each row's engine and maximum, then approve.";
    }

    private async Task TransitionBatchAsync(BatchContext ctx, Func<NarrationBatch, Guid, NarrationBatch> change, string message)
    {
        var session = ctx.Session;
        if (session.Batch is not { } batch || SelectedBatchRows(ctx) is not [var item]) return;
        session.Batch = await session.Store.SaveAsync(change(batch, item.ItemId)).ConfigureAwait(true);
        ctx.Status.Text = message;
        await ReviewBatchAsync(ctx).ConfigureAwait(true);
    }
}
