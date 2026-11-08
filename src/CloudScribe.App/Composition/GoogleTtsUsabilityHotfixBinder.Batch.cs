using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private const long BatchMaximumSourceBytes = 32L * 1024 * 1024;
    private static readonly FilePickerFileType TextFileType = new("Text files") { Patterns = ["*.txt"], MimeTypes = ["text/plain"] };
    private bool _batchDialogOpen;

    private void AddBatchControl(Window owner, Panel host, ShellViewModel model)
    {
        var button = NewButton("Batch MP3 from TXT files…");
        AutomationProperties.SetName(button, "Make one MP3 for each of several TXT files");
        ToolTip.SetTip(button, "Choose several TXT files or a folder. Each file becomes its own document and its own MP3, with one voice, one review and one approval.");
        button.Click += async (_, _) => await OpenBatchAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
    }

    /// <summary>All of a batch window's mutable state, so handlers stay short.</summary>
    private sealed class BatchSession : IDisposable
    {
        public required FileBatchQueueStore Store { get; init; }
        public required BatchAuthorityGate Gate { get; init; }
        public NarrationBatch? Batch { get; set; }
        public List<BatchItem> Draft { get; } = [];
        public Dictionary<Guid, string> Texts { get; } = [];
        public string? OutputFolder { get; set; }
        public string? Name { get; set; }
        public bool NumberTracks { get; set; }
        public BatchAuthorityProfile? Profile { get; set; }
        public BatchAdmissionRequest? Request { get; set; }
        public BatchAuthorityDecision? Decision { get; set; }
        public string ReviewText { get; set; } = string.Empty;
        public BatchQueueRunner? Runner { get; set; }
        public CancellationTokenSource? Run { get; set; }
        public Task? Active { get; set; }
        public BatchItemProgress? Current { get; set; }
        public Stopwatch RunClock { get; } = new();
        public bool Running => Active is { IsCompleted: false };
        public IReadOnlyList<BatchItem> Items => Batch?.Items ?? Draft;
        public void Dispose() => Store.Dispose();
    }

    private sealed record BatchControls(TextBlock Status, TextBlock Current, CheckBox CaptureConsent);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One modal owns the batch window's controls and lifetimes; queue, authority and spending rules live in separately tested services.")]
    private async Task OpenBatchAsync(Window owner, ShellViewModel model)
    {
        if (_batchDialogOpen) return;
        if (documentLibrary is null || documentImporter is null || documentPreprocessor is null)
        { model.StatusMessage = "Batch import is unavailable in this build."; return; }
        _batchDialogOpen = true;
        var store = new FileBatchQueueStore(paths.RootDirectory);
        var authority = batchAuthority ?? new InstalledBatchAuthorityStore(AppContext.BaseDirectory, paths.RootDirectory);
        using var session = new BatchSession { Store = store, Gate = new BatchAuthorityGate(authority, store, TimeProvider.System) };
        var importer = new BatchDocumentImporter(documentImporter, documentPreprocessor, documentLibrary, store);
        try
        {
            session.Profile = await session.Gate.LoadProfileAsync().ConfigureAwait(true);
            var dialog = CreateArticleExportDialog();
            dialog.Title = "Batch MP3 from TXT files"; dialog.Width = 920; dialog.Height = 800;
            var body = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
            body.Children.Add(CosmicDialogTheme.Heading("Batch MP3 from TXT files",
                "Each TXT file becomes its own document and its own MP3, in the order shown. Source files are only read, never changed."));

            var status = NewDescription("Add TXT files or a folder to begin. Nothing is sent to Google until the batch is approved and you choose Start.");
            AutomationProperties.SetName(status, "Batch status");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var voiceText = NewDescription(string.Empty);
            var authorityText = NewDescription(BatchDialogPresentation.Authority(session.Profile));
            var reviewText = NewDescription(string.Empty);
            var summary = NewDescription(string.Empty);
            var timing = NewDescription(string.Empty);
            var current = NewDescription(string.Empty);
            AutomationProperties.SetName(current, "Current file progress");
            var overall = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12 };
            AutomationProperties.SetName(overall, "Batch progress by finished files");
            var list = new ListBox
            {
                MinHeight = 220,
                MaxHeight = 320,
                Background = CosmicDialogTheme.Solid(CosmicDialogTheme.Raised),
                BorderBrush = CosmicDialogTheme.Solid(CosmicDialogTheme.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
            };
            AutomationProperties.SetName(list, "Files in this batch, in narration order");

            var saved = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Open a saved batch…" };
            AutomationProperties.SetName(saved, "Open a saved batch");
            var engine = new ComboBox { ItemsSource = BatchDialogPresentation.EngineChoices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(engine, "Narration engine for this batch");
            var policy = new ComboBox { ItemsSource = new[] { "If a file fails, skip it and continue", "If a file fails, stop the batch" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(policy, "What to do when a file fails");
            var numbering = new CheckBox { Content = NewDescription("Number the MP3 files in this order (01, 02, …). Titles that already start with a number are not numbered twice.") };
            var capture = new CheckBox { Content = NewDescription("For Long Audio files: save bounded, encrypted, redacted provider responses privately for each job (required by Long Audio). They may contain article fragments; they are excluded from normal logs and exports, expire logically after 24 hours and are deleted on next access; encrypted remnants may remain while closed or in backups.") };
            var addFiles = NewButton("Add TXT files…");
            var addFolder = NewButton("Add a folder…");
            var chooseOutput = NewButton("Choose output folder…");
            var newBatch = NewButton("New batch");
            ToolTip.SetTip(newBatch, "Start a new batch. This saved batch stays saved; reopen it any time.");
            var up = NewButton("Move up"); var down = NewButton("Move down"); var remove = NewButton("Remove");
            var retry = NewButton("Retry failed file"); var checkAgain = NewButton("Check again"); var skip = NewButton("Skip file");
            ToolTip.SetTip(retry, "Runs the file again through its saved job, so accepted audio is reused. A failed local copy is copied again without new speech.");
            ToolTip.SetTip(checkAgain, "Asks the saved job what happened. It continues the same job and never sends the file as a new request.");
            var save = NewButton("Add to library and save batch");
            var rehearse = NewButton("Rehearse (no cost)");
            ToolTip.SetTip(rehearse, "Checks each file's real admission and maximum, the output folder, names, audio tools and approval, without contacting Google.");
            var approve = NewButton("Approve batch");
            var export = NewButton("Export batch request for review…");
            var start = NewButton("Start");
            var pause = NewButton("Pause after this file");
            var stopNow = NewButton("Stop now");
            ToolTip.SetTip(stopNow, "Stops at the next safe point. A request already sent may still finish and be charged; it is checked, never resent.");
            var manifest = NewButton("Write playlist and summary");
            var close = NewButton("Close");
            start.Classes.Add("primary"); approve.Classes.Add("primary");
            var controls = new BatchControls(status, current, capture);

            bool refreshing = false;
            string? shownRows = null;
            void Refresh()
            {
                if (refreshing) return;
                refreshing = true;
                try { RefreshCore(); } finally { refreshing = false; }
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Pure enable/visible/text mapping from session state.")]
            void RefreshCore()
            {
                var items = session.Items;
                // Rebuild the rows only when their content changes, so the user's selection survives progress updates.
                string rows = string.Join('\u001e', items.Select(x => BatchDialogPresentation.Row(x, session.Profile)));
                if (!string.Equals(rows, shownRows, StringComparison.Ordinal))
                {
                    int selected = list.SelectedIndex;
                    shownRows = rows;
                    list.ItemsSource = items.Count == 0
                        ? new Control[] { new TextBlock { Text = "No files yet. Choose Add TXT files… or Add a folder…, or open a saved batch.", Foreground = CosmicDialogTheme.Solid("#C6BCCF"), Margin = new Thickness(10), TextWrapping = Avalonia.Media.TextWrapping.Wrap } }
                        : items.Select(x => BatchRowView(x, session.Profile)).ToArray();
                    list.SelectedIndex = selected >= 0 && selected < items.Count ? selected : -1;
                }
                var item = list.SelectedIndex >= 0 && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null;
                bool running = session.Running, draft = session.Batch is null;
                var voice = model.TryCaptureBatchVoice(out string? voiceProblem);
                voiceText.Text = session.Batch is { } b
                    ? $"Voice: {b.Settings.VoiceLabel} · {BatchEngines.Describe(b.Settings.Engine)} · {(b.Settings.FailurePolicy == BatchFailurePolicy.StopBatch ? "stop on failure" : "skip failures and continue")}\nOutput folder: {b.OutputFolder}"
                    : voice is null ? voiceProblem ?? string.Empty
                    : $"Voice for every file: {ShellViewModel.BatchSettingsFor(voice, BatchFailurePolicy.SkipAndContinue).VoiceLabel}\nOutput folder: {session.OutputFolder ?? "not chosen"}";
                addFiles.IsEnabled = addFolder.IsEnabled = draft && !running;
                newBatch.IsVisible = !draft; newBatch.IsEnabled = !running;
                save.IsVisible = policy.IsVisible = engine.IsVisible = numbering.IsVisible = draft;
                chooseOutput.IsEnabled = draft && !running && session.Draft.Count > 0;
                policy.IsEnabled = engine.IsEnabled = numbering.IsEnabled = draft && !running;
                bool movable = !running && item is { IsIncluded: true, State: BatchItemState.Ready, JobId: null };
                up.IsEnabled = movable && item!.Order > 1;
                down.IsEnabled = movable && item!.Order < items.Count;
                remove.IsEnabled = movable;
                retry.IsEnabled = !running && session.Batch is not null && item?.State == BatchItemState.Failed;
                checkAgain.IsEnabled = !running && session.Batch is not null && item?.State == BatchItemState.Uncertain;
                skip.IsEnabled = !running && session.Batch is not null && item is { State: BatchItemState.Ready or BatchItemState.Failed or BatchItemState.Uncertain };
                save.IsEnabled = draft && !running && session.OutputFolder is not null && session.Draft.Any(x => x.IsIncluded) && voice is not null;
                rehearse.IsEnabled = !running && session.Batch is not null;
                var decision = session.Decision;
                approve.IsVisible = session.Profile?.Mode == BatchAuthorityMode.InAppWithinLimits;
                approve.IsEnabled = !running && session.Request is not null && decision is { Allowed: false };
                if (session.Request is { } request && session.Profile is { } p)
                    approve.Content = NewDescription($"Approve {request.ProposedConsent.Items.Count} files, up to {BatchMoney.Format(request.TotalEstimatedMaximumMinorUnits, p)}");
                export.IsVisible = session.Profile?.Mode == BatchAuthorityMode.Reviewed;
                export.IsEnabled = !running && session.Request is not null && decision is { Allowed: false };
                bool longAudioFiles = session.Batch?.Included.Any(x => string.Equals(x.Engine, BatchEngines.LongAudio, StringComparison.Ordinal) && x.State != BatchItemState.Completed) == true;
                capture.IsVisible = longAudioFiles;
                capture.IsEnabled = !running;
                start.IsEnabled = !running && decision is { Allowed: true } && session.Batch is { State: not BatchQueueState.Finished } && (!longAudioFiles || capture.IsChecked == true);
                start.Content = NewDescription(session.Batch?.FirstStartedUtc is null ? "Start" : "Resume");
                pause.IsEnabled = stopNow.IsEnabled = running;
                manifest.IsEnabled = !running && session.Batch?.CompletedCount > 0;
                close.IsEnabled = !running;
                authorityText.Text = BatchDialogPresentation.Authority(session.Profile) + (decision is { } d ? "\n" + d.Explanation : string.Empty);
                reviewText.Text = session.ReviewText;
                reviewText.IsVisible = session.ReviewText.Length > 0;
                if (session.Batch is { } shown)
                {
                    summary.Text = BatchDialogPresentation.Summary(shown, session.Profile);
                    timing.Text = BatchDialogPresentation.Timing(shown, session.RunClock.Elapsed);
                    overall.Value = BatchDialogPresentation.OverallPercentage(shown, session.Current);
                }
                else
                {
                    int included = session.Draft.Count(x => x.IsIncluded);
                    int same = session.Draft.Count(x => x.IsIncluded && x.SameTextAs is not null);
                    summary.Text = session.Draft.Count == 0 ? string.Empty
                        : $"{included} of {session.Draft.Count} files will be narrated, one MP3 each. Use Move up and Move down to change the order."
                          + (same > 0 ? $" {same} file(s) have the same text as an earlier file; they are kept as their own tracks." : string.Empty);
                    timing.Text = string.Empty; overall.Value = 0;
                }
            }

            // Every handler is guarded: a disk, permission or concurrency problem is shown here and never closes the app.
            void On(Button button, Func<Task> action) => button.Click += async (_, _) => await GuardAsync(action).ConfigureAwait(true);
            async Task GuardAsync(Func<Task> action)
            {
                try { await action().ConfigureAwait(true); }
                catch (BatchConcurrencyException error) { status.Text = error.Message + " Reopen the batch from Open a saved batch to see its latest state."; }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = "That did not complete: " + AudioOutputPresentation.RecoveryAdvice(error) + " Nothing was sent to Google."; }
                finally { Refresh(); }
            }

            async Task ReviewAsync()
            {
                session.Request = null; session.Decision = null; session.ReviewText = string.Empty;
                if (session.Batch is not { } batch) return;
                if (model.TryCaptureBatchVoice(out string? voiceProblem) is not { } voice)
                { session.ReviewText = voiceProblem ?? "Select the batch voice to review costs and approval."; return; }
                session.Decision = await session.Gate.CheckAsync(batch, voice.AccountStableId, voice.ProjectStableId).ConfigureAwait(true);
                if (session.Profile is not { } profile || session.Decision.Allowed || batch.State == BatchQueueState.Finished) return;
                status.Text = "Reviewing each file's engine and maximum (no request is sent)…";
                var previous = await session.Gate.LoadConsentAsync(batch.BatchId).ConfigureAwait(true);
                var request = await BatchRequestBuilder(voice).BuildAsync(batch, profile, voice.AccountStableId, voice.ProjectStableId, previous).ConfigureAwait(true);
                session.Request = request;
                session.Batch = await SaveReviewedEnginesAsync(store, batch, request).ConfigureAwait(true);
                int la = request.Items.Count(x => x.LongAudio is not null), ordinary = request.Items.Count(x => string.Equals(x.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal));
                session.ReviewText = BatchDialogPresentation.ReviewSummary(la, ordinary, request.NeedingArticleAdmission.Count(), request.Unnarratable.Count(),
                    request.TotalEstimatedMaximumMinorUnits, request.LongAudioMaximumUsdCents, request.LongAudioHeadroomUsdCents, profile);
                status.Text = "Review ready. Check each row's engine and maximum, then approve.";
            }

            async Task LoadSavedListAsync()
            {
                var batches = await store.ListAsync().ConfigureAwait(true);
                saved.ItemsSource = batches.Select(x => new ComboBoxItem
                {
                    Content = $"{x.Name} — {BatchDialogPresentation.QueueState(x.State)}, {x.CompletedCount}/{x.IncludedCount} done, created {x.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}",
                    Tag = x.BatchId,
                }).ToArray();
            }

            bool OutputExists(string name) => session.OutputFolder is { } folder && File.Exists(Path.Combine(folder, name));
            string RenamedText(IReadOnlyList<BatchItem> items)
            {
                var renamed = BatchPlanner.Renamed(items, t => t + ".mp3", session.NumberTracks);
                return renamed.Count == 0 ? string.Empty
                    : $" {renamed.Count} name(s) already exist in the output folder or batch, so a number in brackets was added (for example {renamed[0].OutputFileName}). Nothing is ever overwritten; choose another output folder to avoid this.";
            }

            async Task AddAsync(IReadOnlyList<IStorageFile> files, string? folderName)
            {
                status.Text = $"Reading {files.Count} files…";
                var read = new List<BatchSourceFile>();
                foreach (var file in files)
                {
                    try
                    {
                        var stream = await file.OpenReadAsync().ConfigureAwait(true);
                        await using (stream.ConfigureAwait(true))
                            read.Add(await importer.ReadAsync(file.Name, stream, BatchMaximumSourceBytes).ConfigureAwait(true));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { read.Add(new(file.Name, string.Empty, 0, null, BatchExclusion.Unreadable)); }
                    session.OutputFolder ??= file.TryGetLocalPath() is { } local ? Path.GetDirectoryName(local) : null;
                }
                session.Name ??= folderName ?? $"Batch {DateTime.Now:d MMM yyyy HH:mm}";
                // New files go after the current list (whose order the user may have arranged); only they are sorted.
                var before = session.Draft.Select(x => x.ItemId).ToHashSet();
                var planned = BatchPlanner.Append(session.Draft, read, t => t + ".mp3", OutputExists, session.NumberTracks);
                var textByFile = read.Where(x => x.Text is not null).GroupBy(x => (x.FileName, x.Sha256)).ToDictionary(g => g.Key, g => g.First().Text!);
                session.Draft.Clear(); session.Draft.AddRange(planned);
                foreach (var item in planned.Where(x => !before.Contains(x.ItemId) && x.IsIncluded))
                    if (textByFile.TryGetValue((item.SourceFileName, item.SourceSha256), out string? text)) session.Texts[item.ItemId] = text;
                int skipped = planned.Count(x => !before.Contains(x.ItemId) && !x.IsIncluded);
                status.Text = $"{session.Draft.Count} files listed" + (skipped > 0 ? $"; {skipped} new file(s) cannot be used (see each row)." : ".")
                    + RenamedText(session.Draft) + " Check the order and output names, then choose Add to library and save batch.";
            }

            On(addFiles, async () =>
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose TXT files", AllowMultiple = true, FileTypeFilter = [TextFileType] }).ConfigureAwait(true);
                if (files.Count > 0) await AddAsync(files, null).ConfigureAwait(true);
            });
            On(addFolder, async () =>
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose a folder of TXT files", AllowMultiple = false }).ConfigureAwait(true);
                if (folders.Count == 0) return;
                var files = new List<IStorageFile>();
                await foreach (var entry in folders[0].GetItemsAsync().ConfigureAwait(true))
                    if (entry is IStorageFile file && file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) files.Add(file);
                if (files.Count == 0) { status.Text = "That folder has no .txt files (subfolders are not included)."; return; }
                session.OutputFolder ??= folders[0].TryGetLocalPath();
                await AddAsync(files, folders[0].Name).ConfigureAwait(true);
            });
            void Rename()
            {
                var renamed = BatchPlanner.AssignOutputNames(session.Draft, t => t + ".mp3", OutputExists, session.NumberTracks);
                session.Draft.Clear(); session.Draft.AddRange(renamed);
            }
            On(chooseOutput, async () =>
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose where to save the MP3 files", AllowMultiple = false }).ConfigureAwait(true);
                if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
                session.OutputFolder = path;
                Rename();
                status.Text = "Output folder chosen." + RenamedText(session.Draft);
            });
            numbering.IsCheckedChanged += (_, _) => { session.NumberTracks = numbering.IsChecked == true; Rename(); status.Text = (session.NumberTracks ? "Track numbers added." : "Track numbers removed.") + RenamedText(session.Draft); Refresh(); };
            async Task ChangeOrderAsync(Func<IReadOnlyList<BatchItem>, BatchItem, IReadOnlyList<BatchItem>> change)
            {
                var items = session.Items;
                if (list.SelectedIndex < 0 || list.SelectedIndex >= items.Count) return;
                var target = items[list.SelectedIndex];
                string folder = session.Batch?.OutputFolder ?? session.OutputFolder ?? string.Empty;
                bool numbers = session.Batch?.NumberTracks ?? session.NumberTracks;
                var updated = BatchPlanner.AssignOutputNames(change(items, target), t => t + ".mp3", n => File.Exists(Path.Combine(folder, n)), numbers);
                if (session.Batch is { } batch) { session.Batch = await store.SaveAsync(batch with { Items = updated }).ConfigureAwait(true); await ReviewAsync().ConfigureAwait(true); }
                else { session.Draft.Clear(); session.Draft.AddRange(updated); }
                list.SelectedIndex = updated.ToList().FindIndex(x => x.ItemId == target.ItemId);
            }
            On(up, () => ChangeOrderAsync((items, x) => BatchPlanner.Move(items, x.ItemId, -1)));
            On(down, () => ChangeOrderAsync((items, x) => BatchPlanner.Move(items, x.ItemId, 1)));
            On(remove, () => ChangeOrderAsync((items, x) => BatchPlanner.Remove(items, x.ItemId)));
            async Task TransitionAsync(Func<NarrationBatch, Guid, NarrationBatch> change, string message)
            {
                if (session.Batch is not { } batch || list.SelectedIndex < 0 || list.SelectedIndex >= batch.Items.Count) return;
                session.Batch = await store.SaveAsync(change(batch, batch.Items[list.SelectedIndex].ItemId)).ConfigureAwait(true);
                status.Text = message; await ReviewAsync().ConfigureAwait(true);
            }
            On(retry, () => TransitionAsync(BatchQueueRunner.Retry, "The file will run again when you resume. Its saved job is continued, so accepted audio is reused."));
            On(checkAgain, () => TransitionAsync(BatchQueueRunner.CheckAgain, "The saved job will be checked when you resume. It is never sent again as a new request."));
            On(skip, () => TransitionAsync(BatchQueueRunner.Skip, "The file is skipped. Any hold it already has stays until reconciled."));

            On(save, async () =>
            {
                if (model.TryCaptureBatchVoice(out string? problem) is not { } voice) { status.Text = problem ?? string.Empty; return; }
                var settings = ShellViewModel.BatchSettingsFor(voice, policy.SelectedIndex == 1 ? BatchFailurePolicy.StopBatch : BatchFailurePolicy.SkipAndContinue)
                    with
                { Engine = BatchDialogPresentation.EngineForChoice(engine.SelectedIndex) };
                Rename(); // Names are checked against the folder once more right before saving.
                var batch = new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), session.Name ?? "Batch", 0, BatchQueueState.Ready, settings, settings.Sha256(),
                    session.OutputFolder!, session.Profile?.Currency ?? "USD", session.Profile?.Scale ?? 2,
                    session.Draft.Select(x => x with { EstimatedMaximumMinorUnits = session.Profile?.EstimateMinorUnits(x.Characters) ?? 0 }).ToArray(),
                    DateTimeOffset.UtcNow, NumberTracks: session.NumberTracks);
                save.IsEnabled = false;
                status.Text = "Saving the batch and adding each file to the library…";
                batch = await store.SaveAsync(batch).ConfigureAwait(true);
                session.Batch = await importer.ImportAsync(batch, session.Texts).ConfigureAwait(true);
                session.Texts.Clear(); session.Draft.Clear();
                if (catalogLogger is not null) CloudScribe.Application.Logging.CloudScribeLog.BatchCreated(catalogLogger, session.Batch.BatchId, session.Batch.Items.Count, session.Batch.IncludedCount);
                model.RefreshDocumentLibraryAfterBatchImport();
                await LoadSavedListAsync().ConfigureAwait(true);
                await ReviewAsync().ConfigureAwait(true);
                status.Text = $"Saved. {session.Batch!.IncludedCount} documents were added to the library. The batch survives closing the app; reopen it from Open a saved batch.";
            });
            saved.SelectionChanged += async (_, _) => await GuardAsync(async () =>
            {
                if (session.Running || saved.SelectedItem is not ComboBoxItem { Tag: Guid id }) return;
                session.Batch = await store.LoadAsync(id).ConfigureAwait(true);
                session.Draft.Clear(); session.Texts.Clear();
                status.Text = session.Batch is null ? "That batch could not be read." : $"Opened {session.Batch.Name}.";
                await ReviewAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
            list.SelectionChanged += (_, _) => Refresh();
            capture.IsCheckedChanged += (_, _) => Refresh();
            newBatch.Click += (_, _) =>
            {
                if (session.Running) return;
                session.Batch = null; session.Request = null; session.Decision = null; session.ReviewText = string.Empty;
                session.Draft.Clear(); session.Texts.Clear(); session.OutputFolder = null; session.Name = null; saved.SelectedItem = null;
                status.Text = "New batch. Add TXT files or a folder. The previous batch is still saved."; Refresh();
            };

            On(rehearse, async () => status.Text = await RehearseBatchAsync(session, model).ConfigureAwait(true));
            On(export, async () =>
            {
                if (session.Request is not { } request) return;
                if (request.Unnarratable.Any()) { status.Text = "Remove the files that no allowed engine can take before exporting."; return; }
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose where to save the batch request", AllowMultiple = false }).ConfigureAwait(true);
                if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
                string path = await BatchAdmissionRequestBuilder.WriteAsync(request, folder).ConfigureAwait(true);
                status.Text = $"Batch request saved to {path}.\nIt contains hashes and limits, not your text. Installing it once (Install-BatchAuthority.ps1) admits every Long Audio file in it; ordinary-engine files also need their article admission. Then reopen this batch and choose Start.";
            });
            On(approve, async () =>
            {
                if (session.Batch is not { } batch || session.Request is not { } request || model.TryCaptureBatchVoice(out _) is not { } voice) return;
                approve.IsEnabled = false;
                var result = await session.Gate.ApproveInAppAsync(batch, voice.AccountStableId, voice.ProjectStableId, request).ConfigureAwait(true);
                status.Text = result.Allowed ? "Approved. Choose Start when ready." : result.Explanation;
                await ReviewAsync().ConfigureAwait(true);
            });
            start.Click += (_, _) =>
            {
                if (session.Running) return;
                session.Active = RunBatchAsync(dialog, model, session, controls, Refresh);
                Refresh();
            };
            pause.Click += (_, _) => { session.Runner?.RequestPause(); status.Text = "Pausing after the current file finishes. Nothing new will start."; };
            stopNow.Click += (_, _) => { session.Run?.Cancel(); status.Text = "Stopping at the next safe point. A request already sent is checked, never resent."; };
            On(manifest, () =>
            {
                if (session.Batch is not { } batch) return Task.CompletedTask;
                var (playlist, csv) = BatchManifestWriter.Write(batch);
                status.Text = $"Wrote {Path.GetFileName(playlist)} and {Path.GetFileName(csv)} in the output folder.";
                return Task.CompletedTask;
            });
            close.Click += (_, _) => dialog.Close();
            dialog.Closing += (_, e) => { if (session.Running) { e.Cancel = true; status.Text = "Pause or stop the batch before closing this window."; } };
            var clock = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (_, _) => { if (session.Running) Refresh(); };
            clock.Start();

            Control Row(params Control[] items)
            {
                var grid = new UniformGrid { Rows = 1, Columns = items.Length };
                foreach (var control in items) { control.Margin = new Thickness(0, 0, 8, 0); grid.Children.Add(control); }
                return grid;
            }
            body.Children.Add(saved);
            body.Children.Add(Row(addFiles, addFolder, chooseOutput, newBatch));
            body.Children.Add(voiceText);
            body.Children.Add(Row(engine, policy));
            body.Children.Add(numbering);
            body.Children.Add(list);
            body.Children.Add(Row(up, down, remove, retry, checkAgain, skip));
            body.Children.Add(summary);
            body.Children.Add(reviewText);
            body.Children.Add(authorityText);
            body.Children.Add(capture);
            body.Children.Add(overall);
            body.Children.Add(current);
            body.Children.Add(timing);
            body.Children.Add(status);
            var footer = new StackPanel { Margin = new Thickness(22, 12), Spacing = 8 };
            footer.Children.Add(Row(save, rehearse, approve, export));
            footer.Children.Add(Row(start, pause, stopNow, manifest, close));
            var layout = new DockPanel();
            var bar = CosmicDialogTheme.ActionBar(footer); DockPanel.SetDock(bar, Dock.Bottom);
            layout.Children.Add(bar);
            layout.Children.Add(new ScrollViewer { Content = body });
            dialog.Content = layout;
            await LoadSavedListAsync().ConfigureAwait(true);
            Refresh();
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            clock.Stop();
            if (session.Active is { } active) await active.ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "The batch window could not continue: " + AudioOutputPresentation.RecoveryAdvice(error); }
        finally { _batchDialogOpen = false; }
    }

    /// <summary>The request builder with both engines: Long Audio (installed scope plus batch approvals) and the ordinary
    /// engine's exact review for files it has already admitted. Building it sends nothing and commits nothing.</summary>
    private BatchAdmissionRequestBuilder BatchRequestBuilder(ShellViewModel.GoogleTtsStudioRequestSelection voice)
    {
        var planner = longAudioInstalled is not null && batchLongAudioExtension is not null ? new BatchLongAudioPlanner(longAudioInstalled, batchLongAudioExtension) : null;
        var articles = chirpArticles is not null && documentLibrary is not null
            ? new BatchArticleWorkflow(chirpArticles, documentLibrary, (_, _, _) => throw new InvalidOperationException("Review only."), voice)
            : null;
        return new BatchAdmissionRequestBuilder(documentLibrary!, TimeProvider.System, planner, articles);
    }

    /// <summary>Shows each not-yet-started file's reviewed engine and maximum in the saved batch.</summary>
    private static async Task<NarrationBatch> SaveReviewedEnginesAsync(FileBatchQueueStore store, NarrationBatch batch, BatchAdmissionRequest request)
    {
        bool changed = false;
        var items = batch.Items.Select(x =>
        {
            if (x.JobId is not null || request.Items.SingleOrDefault(r => r.ItemId == x.ItemId) is not { } reviewed) return x;
            if (string.Equals(x.Engine, reviewed.Engine, StringComparison.Ordinal) && x.EstimatedMaximumMinorUnits == reviewed.EstimatedMaximumMinorUnits) return x;
            changed = true;
            return x with { Engine = reviewed.Engine, EstimatedMaximumMinorUnits = reviewed.EstimatedMaximumMinorUnits };
        }).ToArray();
        return changed ? await store.SaveAsync(batch with { Items = items }).ConfigureAwait(true) : batch;
    }

    private static Border BatchRowView(BatchItem item, BatchAuthorityProfile? profile)
    {
        string accent = BatchDialogPresentation.Tone(item) switch
        {
            "done" => "#7FD4A0",
            "attention" => CosmicDialogTheme.Gold,
            "failed" => "#FF8A80",
            "waiting" => CosmicDialogTheme.Cyan,
            _ => "#8C819A",
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = BatchDialogPresentation.Title(item), Foreground = CosmicDialogTheme.Solid(item.IsIncluded ? CosmicDialogTheme.TextOnDark : "#A79DB3"), FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = BatchDialogPresentation.Detail(item, profile), Foreground = CosmicDialogTheme.Solid("#C6BCCF"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var row = new Border { BorderBrush = CosmicDialogTheme.Solid(accent), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 4), Child = text };
        AutomationProperties.SetName(row, BatchDialogPresentation.Row(item, profile).Replace("\n", ". ", StringComparison.Ordinal));
        return row;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One awaited run owns its progress, tools and lifetime; queue safety rules are in the tested BatchQueueRunner.")]
    private async Task RunBatchAsync(Window dialog, ShellViewModel model, BatchSession session, BatchControls ui, Action refresh)
    {
        if (session.Batch is not { } batch || documentLibrary is null) return;
        using var run = new CancellationTokenSource(); // A fresh stop signal for every run, so Resume works after Stop now.
        session.Run = run;
        var status = ui.Status;
        try
        {
            if (model.TryCaptureBatchVoice(out string? problem) is not { } voice) { status.Text = problem ?? string.Empty; return; }
            var tools = await ChooseExistingMediaToolsAsync(dialog, status).ConfigureAwait(true);
            if (tools is null) { status.Text = "Choose the audio tools (ffmpeg.exe with ffprobe.exe) before starting. Nothing was sent."; return; }
            // Names for files that have not started are re-checked now, so a file created meanwhile is never overwritten.
            batch = await session.Store.SaveAsync(batch with
            {
                Items = BatchPlanner.AssignOutputNames(batch.Items, t => t + ".mp3", n => File.Exists(Path.Combine(batch.OutputFolder, n)), batch.NumberTracks),
            }).ConfigureAwait(true);
            session.Batch = batch;
            var engines = new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal);
            if (chirpArticles is not null && chirpPreparation is not null && wholeArticleRequests is not null && wholeArticleResolver is not null)
            {
                var workflow = new BatchArticleWorkflow(chirpArticles, documentLibrary,
                    (selection, destination, observe) => new ChirpWholeArticleRuntime(chirpPreparation, chirpArticles, wholeArticleRequests, wholeArticleResolver,
                        articleAudio, selection, destination, tools.EncoderPath, () => !model.HasCurrentVoiceLanguageCatalog,
                        token => RefreshBatchSelectionOnUiAsync(model, selection, token), null, catalogLogger, observe, trendHistory, approvedMediaTools: tools),
                    voice);
                engines[BatchEngines.CompleteArticle] = new CompleteArticleBatchNarrator(session.Gate, workflow, () => (voice.AccountStableId, voice.ProjectStableId));
            }
            if (longAudioDocuments is not null)
            {
                string probe = Path.Combine(Path.GetDirectoryName(tools.EncoderPath)!, "ffprobe.exe");
                var longAudioTools = new GoogleLongAudioMediaTools(tools.EncoderPath, tools.EncoderSha256, probe, tools.ProbeSha256);
                engines[BatchEngines.LongAudio] = new LongAudioBatchNarrator(session.Gate,
                    new BatchLongAudioWorkflow(longAudioDocuments, longAudioTools, ui.CaptureConsent.IsChecked == true));
            }
            var router = new RoutingBatchNarrator(session.Gate, () => (voice.AccountStableId, voice.ProjectStableId), engines);
            session.Runner = new BatchQueueRunner(session.Store, router, TimeProvider.System, catalogLogger);
            string currentSettings = (batch.Settings with { LanguageCode = voice.LanguageCode, VoiceStableId = voice.VoiceStableId }).Sha256();
            var progress = new Progress<NarrationBatch>(b => { session.Batch = b; refresh(); });
            var itemProgress = new Progress<BatchItemProgress>(p =>
            {
                session.Current = p;
                var item = session.Batch?.Items.FirstOrDefault(x => x.ItemId == p.ItemId);
                ui.Current.Text = item is null ? p.Stage : $"File {item.Order}: {item.SourceFileName} — {p.Stage}"
                    + (p.TotalParts > 0 ? $" ({p.SavedParts:N0}/{p.TotalParts:N0} verified)" : string.Empty);
            });
            session.RunClock.Restart();
            status.Text = "Running. Files are narrated one at a time; finished files are kept even if a later file fails.";
            await model.RunBatchOperationAsync(async token =>
                session.Batch = await session.Runner.RunAsync(batch.BatchId, currentSettings, progress, itemProgress, token).ConfigureAwait(true),
                run.Token).ConfigureAwait(true);
            status.Text = session.Batch?.State == BatchQueueState.Finished
                ? "Batch finished. Each finished file was verified in the output folder."
                : "Batch paused. Choose Resume to continue; finished files are kept and nothing finished is sent again.";
            if (session.Batch is { State: BatchQueueState.Finished, CompletedCount: > 0 } done)
            {
                try { BatchManifestWriter.Write(done); status.Text += " A playlist and summary were written to the output folder."; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text += " The playlist could not be written."; }
            }
        }
        catch (BatchSettingsChangedException) { status.Text = $"Select the batch's voice ({batch.Settings.VoiceLabel}) again to continue. Nothing was sent."; }
        catch (BatchNotAuthorizedException error) { status.Text = error.Message; }
        catch (BatchAlreadyRunningException) { status.Text = "This batch is already running in another window."; }
        catch (OperationCanceledException) { status.Text = "Stopped. Choose Resume to continue; anything already sent is checked, never resent."; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { status.Text = "The batch stopped: " + AudioOutputPresentation.RecoveryAdvice(error) + " Finished files are kept; files that may have been sent are marked Needs checking."; }
        finally
        {
            session.RunClock.Stop();
            session.Current = null; ui.Current.Text = string.Empty;
            session.Run = null;
            try { if (await session.Store.LoadAsync(batch.BatchId).ConfigureAwait(true) is { } latest) session.Batch = latest; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text += " The saved batch could not be re-read; reopen it."; }
            session.Active = null; refresh();
        }
    }

    private async Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshBatchSelectionOnUiAsync(
        ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection original, CancellationToken token) =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await RefreshExactVoiceAsync(model, original, token).ConfigureAwait(true);
            var voice = model.TryCaptureBatchVoice(out _) ?? throw new InvalidOperationException("The batch voice is no longer verified. No request was sent.");
            return ShellViewModel.ForBatchDocument(voice, original.DocumentId, original.RevisionId!.Value, original.ExactText);
        }).ConfigureAwait(false);

    /// <summary>Checks everything the batch needs without contacting Google or committing anything: each file's real
    /// admission and maximum in its engine, the output folder (by writing and removing a small probe file of its own),
    /// names, audio tools and the approval.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "A linear checklist; each line is one independent read-only check.")]
    private async Task<string> RehearseBatchAsync(BatchSession session, ShellViewModel model)
    {
        if (session.Batch is not { } batch || documentLibrary is null) return "Save the batch first.";
        var lines = new List<string> { "Rehearsal (no cost, nothing sent):" };
        if (model.TryCaptureBatchVoice(out string? problem) is not { } voice) return problem ?? "Select the batch voice.";
        lines.Add(string.Equals(voice.VoiceStableId, batch.Settings.VoiceStableId, StringComparison.Ordinal)
            ? "✓ The batch voice is selected and verified." : $"✗ Select the batch voice {batch.Settings.VoiceLabel}.");
        lines.Add(ProbeWritable(batch.OutputFolder));
        var clashes = batch.Included.Where(x => x.State == BatchItemState.Ready && x.JobId is null && File.Exists(Path.Combine(batch.OutputFolder, x.OutputFileName))).ToArray();
        lines.Add(clashes.Length == 0 ? "✓ No output name clashes with an existing file." : $"• {clashes.Length} output name(s) now exist; they get a number in brackets when you start. Nothing is overwritten.");
        var tools = await MediaToolsStore.InspectAsync().ConfigureAwait(true);
        lines.Add(tools.Selection is not null ? "✓ Your saved audio tools are ready." : "• Audio tools will be asked for when you start.");
        var consent = await session.Gate.LoadConsentAsync(batch.BatchId).ConfigureAwait(true);
        int ready = 0, blocked = 0;
        foreach (var item in batch.Included.Where(x => x.State != BatchItemState.Completed).OrderBy(x => x.Order))
        {
            var approved = consent?.Items.SingleOrDefault(x => x.ItemId == item.ItemId);
            string label = $"File {item.Order:00} ({BatchEngines.Describe(approved?.Engine ?? item.Engine)})";
            if (approved is null) { lines.Add($"• {label}: not approved yet."); blocked++; continue; }
            try
            {
                if (approved.LongAudio is { } la && longAudioDocuments is not null)
                {
                    var review = await longAudioDocuments.ReviewAsync(item.DocumentId!.Value).ConfigureAwait(true);
                    bool same = review.Request.AttemptId == la.AttemptId && string.Equals(review.Request.BindingSha256, la.RequestBindingSha256, StringComparison.Ordinal);
                    bool within = review.Cost.IsAlreadyCommitted || review.Cost.EstimatedSynthesisUsdCents <= la.CeilingUsdCents;
                    lines.Add(same && within ? $"✓ {label}: admitted by Long Audio; exact estimate within its approved ceiling." : $"✗ {label}: the Long Audio request differs from its approval.");
                    if (same && within) ready++; else blocked++;
                }
                else if (string.Equals(approved.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal) && chirpArticles is not null)
                {
                    var review = await chirpArticles.PrepareWholeArticleAsync(item.DocumentId!.Value).ConfigureAwait(true);
                    bool fits = string.Equals(review.PlanSha256, approved.PlanSha256, StringComparison.Ordinal) && review.MaximumAdditionalMinorUnits <= approved.MaximumMinorUnits;
                    lines.Add(fits ? $"✓ {label}: admitted; exact maximum within its approval." : $"✗ {label}: its exact plan or maximum differs from its approval.");
                    if (fits) ready++; else blocked++;
                }
                else { lines.Add($"✗ {label}: this engine is not available in this window."); blocked++; }
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or GoogleLongAudioBudgetExceededException)
            { lines.Add($"✗ {label}: not admitted ({error.Message})"); blocked++; }
        }
        var decision = await session.Gate.CheckAsync(batch, voice.AccountStableId, voice.ProjectStableId).ConfigureAwait(true);
        lines.Add((decision.Allowed ? "✓ " : "• ") + decision.Explanation);
        lines.Add(blocked == 0 ? $"All {ready} remaining file(s) are ready." : $"{ready} ready, {blocked} would stop with nothing sent.");
        return string.Join('\n', lines);
    }

    private static string ProbeWritable(string folder)
    {
        if (!Directory.Exists(folder)) return "✗ The output folder is missing.";
        string probe = Path.Combine(folder, ".cloudscribe-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return "✓ The output folder exists and is writable.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "✗ The output folder cannot be written: " + AudioOutputPresentation.RecoveryAdvice(error); }
    }
}
