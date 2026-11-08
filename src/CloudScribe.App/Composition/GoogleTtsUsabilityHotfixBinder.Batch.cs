using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Batch;

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
        ToolTip.SetTip(button, "Drag in or choose several TXT files or folders. Each file becomes its own document and its own MP3, with one voice, one review and one approval.");
        button.Click += async (_, _) => await OpenBatchAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
    }

    /// <summary>All of a batch window's mutable state, so handlers stay short.</summary>
    private sealed class BatchSession : IDisposable
    {
        public required FileBatchQueueStore Store { get; init; }
        public required FileBatchDraftStore Drafts { get; init; }
        public required BatchAuthorityGate Gate { get; init; }
        public NarrationBatch? Batch { get; set; }
        public List<BatchItem> Draft { get; } = [];
        public Dictionary<Guid, string> Texts { get; } = [];
        public string? OutputFolder { get; set; }
        public string? Name { get; set; }
        public bool NumberTracks { get; set; }
        public int EngineChoice { get; set; }
        public BatchFailurePolicy Policy { get; set; } = BatchFailurePolicy.SkipAndContinue;
        public BatchAuthorityProfile? Profile { get; set; }
        public BatchAdmissionRequest? Request { get; set; }
        public BatchAuthorityDecision? Decision { get; set; }
        public long? ApprovedMinorUnits { get; set; }
        public string ReviewText { get; set; } = string.Empty;
        public string RehearsalText { get; set; } = string.Empty;
        public BatchQueueRunner? Runner { get; set; }
        public CancellationTokenSource? Run { get; set; }
        public Task? Active { get; set; }
        public BatchItemProgress? Current { get; set; }
        public Stopwatch RunClock { get; } = new();
        public List<BatchUndo> Undo { get; } = [];

        /// <summary>A non-run step (reading, adding to the library, review, rehearsal) is in progress; another click waits.</summary>
        public bool Busy { get; set; }
        public CancellationTokenSource? Work { get; set; }
        public bool Running => Active is { IsCompleted: false };
        public bool IsDraft => Batch is null;
        public IReadOnlyList<BatchItem> Items => Batch?.Items ?? Draft;
        public string? Folder => Batch?.OutputFolder ?? OutputFolder;
        public bool Numbered => Batch?.NumberTracks ?? NumberTracks;

        public bool AnyStarted => Batch is { } b && b.Items.Any(x => x.JobId is not null || x.State == BatchItemState.Completed);

        public BatchDraft ToDraft() => new(BatchDraft.CurrentVersion, Name, OutputFolder, NumberTracks,
            BatchDialogPresentation.EngineForChoice(EngineChoice), Policy, Draft.ToArray(), DateTimeOffset.UtcNow);

        public void Dispose() { Store.Dispose(); Work?.Dispose(); }
    }

    /// <summary>One undo step: the arrangement before a change, and for a discarded draft its settings too.</summary>
    private sealed record BatchUndo(IReadOnlyList<BatchItem> Items, string What, BatchDraft? DraftSettings = null);

    private sealed record BatchControls(TextBlock Status, TextBlock Current, CheckBox CaptureConsent);

    /// <summary>A row in the file list. The template builds the visual, so long queues are virtualised.</summary>
    private sealed record BatchRowModel(BatchItem Item, string Text, string? EarlierName);

    /// <summary>Every control of the batch window plus the window's state. Built once per window.</summary>
    private sealed class BatchContext
    {
        public required Window Dialog { get; init; }
        public required ShellViewModel Model { get; init; }
        public required BatchSession Session { get; init; }
        public required BatchDocumentImporter Importer { get; init; }

        public TextBlock Steps { get; } = NewDescription(string.Empty);
        public TextBlock Next { get; } = NewDescription(string.Empty);
        public TextBlock Status { get; } = NewDescription("Drag TXT files or folders into the list, or choose Add TXT files…. Nothing is sent to Google until the batch is approved and you choose Start.");
        public TextBlock VoiceText { get; } = NewDescription(string.Empty);
        public TextBlock OutputText { get; } = NewDescription(string.Empty);
        public TextBlock AuthorityText { get; } = NewDescription(string.Empty);
        public TextBlock ReviewText { get; } = NewDescription(string.Empty);
        public TextBlock Rehearsal { get; } = NewDescription(string.Empty);
        public TextBlock Summary { get; } = NewDescription(string.Empty);
        public TextBlock Timing { get; } = NewDescription(string.Empty);
        public TextBlock Current { get; } = NewDescription(string.Empty);
        public TextBlock Results { get; } = NewDescription(string.Empty);
        public TextBlock DropHint { get; } = NewDescription("Drop TXT files or folders here");
        public TextBlock EmptyHint { get; } = NewDescription("No files yet. Drag TXT files or folders here, or choose Add TXT files… or Add a folder…. A saved batch opens from the list above.");
        public ProgressBar Overall { get; } = new() { Minimum = 0, Maximum = 100, Height = 12 };
        public ProgressBar Work { get; } = new() { Minimum = 0, Maximum = 100, Height = 8, IsVisible = false };
        public ListBox List { get; } = new()
        {
            MinHeight = 200,
            MaxHeight = 380,
            SelectionMode = SelectionMode.Multiple,
            Background = CosmicDialogTheme.Solid(CosmicDialogTheme.Raised),
            BorderBrush = CosmicDialogTheme.Solid(CosmicDialogTheme.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
        };
        public Canvas Overlay { get; } = new() { IsHitTestVisible = false };
        public Rectangle Indicator { get; } = new() { Height = 3, Fill = CosmicDialogTheme.Solid(CosmicDialogTheme.Gold), IsVisible = false, RadiusX = 1.5, RadiusY = 1.5 };
        public Border DropZone { get; } = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Padding = new Thickness(2) };
        public ComboBox Saved { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Open a saved batch or the unsaved draft…" };
        public ComboBox Voice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260, PlaceholderText = "Choose the voice for every file…" };
        public ComboBox Engine { get; } = new() { ItemsSource = BatchDialogPresentation.EngineChoices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260 };
        public ComboBox Policy { get; } = new() { ItemsSource = new[] { "If a file fails, skip it and continue", "If a file fails, stop the batch" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260 };
        public CheckBox Numbering { get; } = new() { Content = NewDescription("Number the MP3 files in this order (01, 02, …). Titles that already start with a number are not numbered twice.") };
        public CheckBox Subfolders { get; } = new() { Content = NewDescription("Include subfolders when adding a folder") };
        public CheckBox Capture { get; } = new() { Content = NewDescription("For Long Audio files: save bounded, encrypted, redacted provider responses privately for each job (required by Long Audio). They may contain article fragments; they are excluded from normal logs and exports, expire logically after 24 hours and are deleted on next access; encrypted remnants may remain while closed or in backups.") };
        public Button AddFiles { get; } = NewButton("Add TXT files…");
        public Button AddFolder { get; } = NewButton("Add a folder…");
        public Button Up { get; } = NewButton("Move up");
        public Button Down { get; } = NewButton("Move down");
        public Button Remove { get; } = NewButton("Remove");
        public Button UndoButton { get; } = NewButton("Undo");
        public Button NewBatch { get; } = NewButton("New batch");
        public Button Discard { get; } = NewButton("Discard draft");
        public Button ChooseOutput { get; } = NewButton("Choose output folder…");
        public Button RefreshVoices { get; } = NewButton("Refresh voices");
        public Button UseBatchVoice { get; } = NewButton("Use this batch's voice");
        public Button Retry { get; } = NewButton("Retry failed file");
        public Button CheckAgain { get; } = NewButton("Check again");
        public Button Skip { get; } = NewButton("Skip file");
        public Button Save { get; } = NewButton("Add to library and save batch");
        public Button Rehearse { get; } = NewButton("Rehearse (no cost)");
        public Button Approve { get; } = NewButton("Approve batch");
        public Button Export { get; } = NewButton("Export batch request for review…");
        public Button Start { get; } = NewButton("Start");
        public Button Pause { get; } = NewButton("Pause after this file");
        public Button StopNow { get; } = NewButton("Stop now");
        public Button Manifest { get; } = NewButton("Write playlist and summary");
        public Button OpenFolder { get; } = NewButton("Open output folder");
        public Button OpenFile { get; } = NewButton("Play selected MP3");
        public Button CancelWork { get; } = NewButton("Cancel");
        public Button Close { get; } = NewButton("Close");

        public bool Refreshing { get; set; }
        public bool CloseWhenIdle { get; set; }
        public string? ShownRows { get; set; }
        public BatchRowDrag? Drag { get; set; }
        public BatchControls Controls => new(Status, Current, Capture);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Builds and shows the window; each behaviour lives in its own method below.")]
    private async Task OpenBatchAsync(Window owner, ShellViewModel model)
    {
        if (_batchDialogOpen) return;
        if (documentLibrary is null || documentImporter is null || documentPreprocessor is null)
        { model.StatusMessage = "Batch import is unavailable in this build."; return; }
        _batchDialogOpen = true;
        var store = new FileBatchQueueStore(paths.RootDirectory, catalogLogger);
        var authority = batchAuthority ?? new InstalledBatchAuthorityStore(AppContext.BaseDirectory, paths.RootDirectory);
        using var session = new BatchSession
        {
            Store = store,
            Drafts = new FileBatchDraftStore(paths.RootDirectory, catalogLogger),
            Gate = new BatchAuthorityGate(authority, store, TimeProvider.System),
        };
        try
        {
            BatchDiagnostics.Action(catalogLogger, null, null, BatchDiagnosticOperation.Window, BatchDiagnosticPhase.Requested);
            session.Profile = await session.Gate.LoadProfileAsync().ConfigureAwait(true);
            var dialog = CreateArticleExportDialog();
            dialog.Title = "Batch MP3 from TXT files"; dialog.Width = 960; dialog.Height = 820; dialog.MinWidth = 560; dialog.MinHeight = 480;
            var ctx = new BatchContext { Dialog = dialog, Model = model, Session = session, Importer = new BatchDocumentImporter(documentImporter, documentPreprocessor, documentLibrary, store) };
            BuildBatchLayout(ctx);
            WireBatchHandlers(ctx);
            WireBatchRows(ctx);
            WireBatchDrop(ctx);
            await RestoreDraftAsync(ctx).ConfigureAwait(true);
            await LoadSavedListAsync(ctx).ConfigureAwait(true);
            var clock = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (_, _) => { if (session.Running) RefreshBatch(ctx); };
            clock.Start();
            model.PropertyChanged += OnModelChanged;
            RefreshBatch(ctx);
            try { await dialog.ShowDialog(owner).ConfigureAwait(true); }
            finally { model.PropertyChanged -= OnModelChanged; clock.Stop(); }
            if (session.Active is { } active) await active.ConfigureAwait(true);
            BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Window, BatchDiagnosticPhase.Returned);

            void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                // The main window's voice catalog or selection changed (for example after Refresh voices).
                if (e.PropertyName is nameof(ShellViewModel.SelectedVoiceLabVoice) or nameof(ShellViewModel.VoiceLabCatalogResults)) RefreshBatch(ctx);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            BatchDiagnostics.Failure(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Window, error);
            model.StatusMessage = "The batch window could not continue: " + AudioOutputPresentation.RecoveryAdvice(error) + " Your unsaved draft is kept.";
        }
        finally { _batchDialogOpen = false; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Declarative layout of one window.")]
    private static void BuildBatchLayout(BatchContext ctx)
    {
        AutomationProperties.SetName(ctx.Status, "Batch status");
        AutomationProperties.SetLiveSetting(ctx.Status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(ctx.Next, "Next step");
        AutomationProperties.SetLiveSetting(ctx.Next, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(ctx.Steps, "Batch steps");
        AutomationProperties.SetName(ctx.Current, "Current file progress");
        AutomationProperties.SetName(ctx.Overall, "Batch progress by finished files");
        AutomationProperties.SetName(ctx.Work, "Progress of the current step");
        AutomationProperties.SetName(ctx.List, "Files in this batch, in narration order. Alt+Up or Alt+Down moves the selected files, Delete removes them, Control+Z undoes.");
        AutomationProperties.SetName(ctx.Saved, "Open a saved batch or the unsaved draft");
        AutomationProperties.SetName(ctx.Voice, "Voice for every file in the batch");
        AutomationProperties.SetName(ctx.Engine, "Narration engine for this batch");
        AutomationProperties.SetName(ctx.Policy, "What to do when a file fails");
        AutomationProperties.SetName(ctx.Results, "Batch results");
        ctx.Next.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        ctx.Steps.Foreground = CosmicDialogTheme.Solid(CosmicDialogTheme.Cyan);
        ctx.Start.Classes.Add("primary"); ctx.Approve.Classes.Add("primary"); ctx.Save.Classes.Add("primary");
        ctx.CancelWork.IsVisible = false;
        foreach (var button in new[] { ctx.Up, ctx.Down, ctx.Remove, ctx.UndoButton, ctx.Save, ctx.Rehearse, ctx.Approve, ctx.Export, ctx.Start, ctx.Retry, ctx.CheckAgain, ctx.Skip, ctx.ChooseOutput, ctx.OpenFile, ctx.OpenFolder, ctx.Manifest })
            ToolTip.SetShowOnDisabled(button, true);
        ToolTip.SetTip(ctx.Up, "Moves the selected files up one place (Alt+Up). You can also drag rows.");
        ToolTip.SetTip(ctx.Down, "Moves the selected files down one place (Alt+Down). You can also drag rows.");
        ToolTip.SetTip(ctx.Remove, "Takes the selected waiting files out of the batch (Delete). Undo brings them back.");
        ToolTip.SetTip(ctx.NewBatch, "Start preparing another batch. Saved batches stay saved; reopen them any time.");
        ToolTip.SetTip(ctx.Discard, "Clears the unsaved draft. Undo brings it back while this window is open.");
        ToolTip.SetTip(ctx.Retry, "Runs the file again through its saved job, so accepted audio is reused. A failed local copy is copied again without new speech.");
        ToolTip.SetTip(ctx.CheckAgain, BatchDialogPresentation.CheckAgainGuidance);
        ToolTip.SetTip(ctx.Rehearse, "Checks the voice, folder, names, audio tools, approval and each file's real admission and maximum, without contacting Google.");
        ToolTip.SetTip(ctx.StopNow, "Stops at the next safe point. A request already sent may still finish and be charged; it is checked, never resent.");
        ToolTip.SetTip(ctx.RefreshVoices, "Reads the verified voice list again from the connected Google account. No narration is requested.");
        ToolTip.SetTip(ctx.CancelWork, "Stops reading or adding files at the next file. Files already added stay added; nothing is sent to Google.");

        var body = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        body.Children.Add(CosmicDialogTheme.Heading("Batch MP3 from TXT files",
            "Each TXT file becomes its own document and its own MP3, in the order shown. Source files are only read, never changed."));
        body.Children.Add(ctx.Steps);
        body.Children.Add(ctx.Next);
        body.Children.Add(ctx.Saved);
        body.Children.Add(Section("1  Files"));
        body.Children.Add(Wrap(ctx.AddFiles, ctx.AddFolder, ctx.Subfolders, ctx.NewBatch, ctx.Discard));
        var listHost = new Grid();
        listHost.Children.Add(ctx.List);
        ctx.EmptyHint.Margin = new Thickness(16); ctx.EmptyHint.IsHitTestVisible = false;
        listHost.Children.Add(ctx.EmptyHint);
        ctx.Overlay.Children.Add(ctx.Indicator);
        listHost.Children.Add(ctx.Overlay);
        var dropStack = new StackPanel { Spacing = 4 };
        ctx.DropHint.HorizontalAlignment = HorizontalAlignment.Center;
        ctx.DropHint.Margin = new Thickness(0, 4);
        dropStack.Children.Add(listHost);
        dropStack.Children.Add(ctx.DropHint);
        ctx.DropZone.Child = dropStack;
        SetDropHighlight(ctx, false);
        body.Children.Add(ctx.DropZone);
        body.Children.Add(Wrap(ctx.Up, ctx.Down, ctx.Remove, ctx.UndoButton));
        body.Children.Add(ctx.Summary);
        body.Children.Add(Section("2  Voice and output"));
        body.Children.Add(Wrap(ctx.Voice, ctx.RefreshVoices, ctx.UseBatchVoice));
        body.Children.Add(ctx.VoiceText);
        body.Children.Add(Wrap(ctx.ChooseOutput));
        body.Children.Add(ctx.OutputText);
        body.Children.Add(Wrap(ctx.Engine, ctx.Policy));
        body.Children.Add(ctx.Numbering);
        body.Children.Add(Section("3  Review and approval"));
        body.Children.Add(ctx.ReviewText);
        body.Children.Add(ctx.Rehearsal);
        body.Children.Add(ctx.AuthorityText);
        body.Children.Add(ctx.Capture);
        body.Children.Add(Section("4  Run"));
        body.Children.Add(ctx.Overall);
        body.Children.Add(ctx.Current);
        body.Children.Add(ctx.Timing);
        body.Children.Add(Section("5  Results"));
        body.Children.Add(ctx.Results);
        body.Children.Add(Wrap(ctx.OpenFolder, ctx.OpenFile, ctx.Manifest, ctx.Retry, ctx.CheckAgain, ctx.Skip));
        var footer = new StackPanel { Margin = new Thickness(22, 10), Spacing = 6 };
        footer.Children.Add(ctx.Status);
        footer.Children.Add(Wrap(ctx.Work, ctx.CancelWork));
        footer.Children.Add(Wrap(ctx.Save, ctx.Rehearse, ctx.Approve, ctx.Export, ctx.Start, ctx.Pause, ctx.StopNow, ctx.Close));
        ctx.Work.Width = 220;
        var layout = new DockPanel();
        var bar = CosmicDialogTheme.ActionBar(footer); DockPanel.SetDock(bar, Dock.Bottom);
        layout.Children.Add(bar);
        layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        ctx.Dialog.Content = layout;

        static Control Wrap(params Control[] items)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var control in items) { control.Margin = new Thickness(0, 0, 8, 6); panel.Children.Add(control); }
            return panel;
        }

        static TextBlock Section(string title) => new()
        {
            Text = title,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = CosmicDialogTheme.Solid(CosmicDialogTheme.TextOnDark),
        };
    }

    private static void SetDropHighlight(BatchContext ctx, bool active)
    {
        ctx.DropZone.BorderBrush = CosmicDialogTheme.Solid(active ? CosmicDialogTheme.Gold : CosmicDialogTheme.BorderSubtle);
        ctx.DropZone.Background = active ? CosmicDialogTheme.Solid("#33F5C26B") : null;
        ctx.DropHint.Text = active ? "Release to add these files after the current list" : "Drop TXT files or folders here (they are added after the current files)";
    }

    /// <summary>Runs one user action with logging, a single-step guard and plain error text. Nothing here sends a request.</summary>
    private async Task GuardBatchAsync(BatchContext ctx, BatchDiagnosticOperation operation, Func<Task> action, bool exclusive = true)
    {
        var session = ctx.Session;
        if (exclusive && (session.Busy || session.Running))
        {
            BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, operation, BatchDiagnosticPhase.Rejected);
            ctx.Status.Text = session.Running ? "The batch is running; pause or stop it first." : "Still finishing the previous step. Try again when it is done.";
            return;
        }
        Guid? itemId = SelectedBatchRows(ctx).FirstOrDefault()?.ItemId;
        bool listHadFocus = ctx.List.IsKeyboardFocusWithin;
        BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, itemId, operation, BatchDiagnosticPhase.Requested);
        if (exclusive) session.Busy = true;
        RefreshBatch(ctx);
        try
        {
            await action().ConfigureAwait(true);
            BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, itemId, operation, BatchDiagnosticPhase.Returned);
        }
        catch (OperationCanceledException error)
        {
            BatchDiagnostics.Failure(catalogLogger, session.Batch?.BatchId, itemId, operation, error);
            ctx.Status.Text = "Cancelled. Everything finished before the cancel is kept; nothing was sent to Google.";
        }
        catch (BatchConcurrencyException error)
        {
            BatchDiagnostics.Failure(catalogLogger, session.Batch?.BatchId, itemId, operation, error);
            ctx.Status.Text = error.Message + " Reopen the batch from the list above to see its latest state.";
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            BatchDiagnostics.Failure(catalogLogger, session.Batch?.BatchId, itemId, operation, error);
            ctx.Status.Text = "That did not complete: " + AudioOutputPresentation.RecoveryAdvice(error) + " Nothing was sent to Google.";
        }
        finally
        {
            if (exclusive) session.Busy = false;
            ctx.Work.IsVisible = ctx.CancelWork.IsVisible = false;
            RefreshBatch(ctx);
            if (listHadFocus) RestoreBatchListFocus(ctx);
            if (ctx.CloseWhenIdle && !session.Busy && !session.Running) ctx.Dialog.Close();
        }
    }

    private void OnBatch(BatchContext ctx, Button button, BatchDiagnosticOperation operation, Func<Task> action) =>
        button.Click += async (_, _) => await GuardBatchAsync(ctx, operation, action).ConfigureAwait(true);

    /// <summary>A cancellable step with a small progress bar, so long queues stay responsive.</summary>
    private static CancellationToken BeginBatchWork(BatchContext ctx)
    {
        ctx.Session.Work?.Dispose();
        ctx.Session.Work = new CancellationTokenSource();
        ctx.Work.Value = 0; ctx.Work.IsVisible = ctx.CancelWork.IsVisible = true;
        return ctx.Session.Work.Token;
    }

    private static void ReportBatchWork(BatchContext ctx, string what, int done, int total)
    {
        ctx.Work.Value = total == 0 ? 100 : 100.0 * done / total;
        ctx.Status.Text = $"{what} {done:N0} of {total:N0}…";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Wires each control to its one handler.")]
    private void WireBatchHandlers(BatchContext ctx)
    {
        var session = ctx.Session;
        OnBatch(ctx, ctx.AddFiles, BatchDiagnosticOperation.AddFiles, async () =>
        {
            var files = await ctx.Dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose TXT files", AllowMultiple = true, FileTypeFilter = [TextFileType] }).ConfigureAwait(true);
            if (files.Count > 0) await AddStorageItemsAsync(ctx, files.Cast<IStorageItem>().ToArray(), null).ConfigureAwait(true);
        });
        OnBatch(ctx, ctx.AddFolder, BatchDiagnosticOperation.AddFolder, async () =>
        {
            var folders = await ctx.Dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose a folder of TXT files", AllowMultiple = true }).ConfigureAwait(true);
            if (folders.Count > 0) await AddStorageItemsAsync(ctx, folders.Cast<IStorageItem>().ToArray(), folders[0].Name).ConfigureAwait(true);
        });
        OnBatch(ctx, ctx.ChooseOutput, BatchDiagnosticOperation.ChooseOutput, () => ChooseBatchOutputAsync(ctx));
        OnBatch(ctx, ctx.Up, BatchDiagnosticOperation.Reorder, () => EditBatchAsync(ctx, "Moved up", (items, ids) => BatchEditor.MoveBy(items, ids, -1)));
        OnBatch(ctx, ctx.Down, BatchDiagnosticOperation.Reorder, () => EditBatchAsync(ctx, "Moved down", (items, ids) => BatchEditor.MoveBy(items, ids, 1)));
        OnBatch(ctx, ctx.Remove, BatchDiagnosticOperation.Remove, () => EditBatchAsync(ctx, "Removed", (items, ids) => BatchEditor.Remove(items, ids, session.IsDraft)));
        OnBatch(ctx, ctx.UndoButton, BatchDiagnosticOperation.Undo, () => UndoBatchAsync(ctx));
        OnBatch(ctx, ctx.Discard, BatchDiagnosticOperation.DiscardDraft, () => DiscardDraftAsync(ctx));
        OnBatch(ctx, ctx.NewBatch, BatchDiagnosticOperation.NewBatch, () => ShowDraftAsync(ctx, "Preparing a new batch. Saved batches stay saved."));
        OnBatch(ctx, ctx.RefreshVoices, BatchDiagnosticOperation.RefreshVoices, async () =>
        {
            ctx.Status.Text = "Reading the verified voice list (no narration is requested)…";
            bool refreshed = await ctx.Model.RefreshBatchVoicesAsync().ConfigureAwait(true);
            ctx.Status.Text = !refreshed
                ? "Voices cannot be refreshed right now. Connect Google in the main window (Google TTS setup), then choose Refresh voices. Your draft is kept, even if you close this window."
                : ctx.Model.BatchVoiceChoices.Count == 0 ? "No verified voices are available. If Google is not connected yet, set it up from the main window (Setups, or Help → Google setup guide), then choose Refresh voices here. Your draft is kept." : $"{ctx.Model.BatchVoiceChoices.Count} verified voices available.";
            await ReviewBatchAsync(ctx).ConfigureAwait(true);
        });
        OnBatch(ctx, ctx.UseBatchVoice, BatchDiagnosticOperation.ChooseVoice, async () =>
        {
            if (session.Batch is not { } batch) return;
            ctx.Status.Text = ctx.Model.TrySelectBatchVoice(batch.Settings.VoiceStableId)
                ? $"Selected {batch.Settings.VoiceLabel}, this batch's voice."
                : $"{batch.Settings.VoiceLabel} is not in the verified voice list right now. Choose Refresh voices; if it is still missing, the account or project may have changed.";
            await ReviewBatchAsync(ctx).ConfigureAwait(true);
        });
        ctx.Voice.SelectionChanged += async (_, _) =>
        {
            if (ctx.Refreshing || ctx.Voice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            await GuardBatchAsync(ctx, BatchDiagnosticOperation.ChooseVoice, async () =>
            {
                ctx.Model.TrySelectBatchVoice(id);
                await ReviewBatchAsync(ctx).ConfigureAwait(true);
            }).ConfigureAwait(true);
        };
        ctx.Engine.SelectionChanged += async (_, _) => await DraftSettingChangedAsync(ctx, () => session.EngineChoice = Math.Max(0, ctx.Engine.SelectedIndex)).ConfigureAwait(true);
        ctx.Policy.SelectionChanged += async (_, _) => await DraftSettingChangedAsync(ctx, () => session.Policy = ctx.Policy.SelectedIndex == 1 ? BatchFailurePolicy.StopBatch : BatchFailurePolicy.SkipAndContinue).ConfigureAwait(true);
        ctx.Numbering.IsCheckedChanged += async (_, _) => await DraftSettingChangedAsync(ctx, () =>
        {
            session.NumberTracks = ctx.Numbering.IsChecked == true;
            ReplaceDraft(session, BatchPlanner.AssignOutputNames(session.Draft, t => t + ".mp3", n => BatchOutputExists(session, n), session.NumberTracks));
            ctx.Status.Text = (session.NumberTracks ? "Track numbers added." : "Track numbers removed.") + RenamedText(session);
        }).ConfigureAwait(true);
        OnBatch(ctx, ctx.Retry, BatchDiagnosticOperation.Retry, () => TransitionBatchAsync(ctx, BatchQueueRunner.Retry, "The file will run again when you resume. Its saved job is continued, so accepted audio is reused."));
        OnBatch(ctx, ctx.CheckAgain, BatchDiagnosticOperation.CheckAgain, () => TransitionBatchAsync(ctx, BatchQueueRunner.CheckAgain, BatchDialogPresentation.CheckAgainGuidance));
        OnBatch(ctx, ctx.Skip, BatchDiagnosticOperation.Skip, () => TransitionBatchAsync(ctx, BatchQueueRunner.Skip, "Skipped. Any hold it already has stays until reconciled."));
        OnBatch(ctx, ctx.Save, BatchDiagnosticOperation.SaveAndImport, () => SaveDraftAsBatchAsync(ctx));
        ctx.Saved.SelectionChanged += async (_, _) =>
        {
            if (ctx.Refreshing || ctx.Saved.SelectedItem is not ComboBoxItem { Tag: Guid id }) return;
            await GuardBatchAsync(ctx, BatchDiagnosticOperation.LoadSaved, () => id == Guid.Empty
                ? ShowDraftAsync(ctx, "Back to the unsaved draft.") : OpenSavedBatchAsync(ctx, id)).ConfigureAwait(true);
        };
        ctx.Capture.IsCheckedChanged += (_, _) => RefreshBatch(ctx);
        OnBatch(ctx, ctx.Rehearse, BatchDiagnosticOperation.Rehearse, async () =>
        {
            ctx.Status.Text = "Rehearsing (no cost, nothing sent)…";
            var report = await RehearseBatchAsync(session, ctx.Model).ConfigureAwait(true);
            session.RehearsalText = report.ToString();
            ctx.Status.Text = report.Verdict + " The full checklist is under Review and approval.";
            if (!report.Ready) BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Rehearse, BatchDiagnosticPhase.Rejected);
        });
        OnBatch(ctx, ctx.Export, BatchDiagnosticOperation.ExportApproval, async () =>
        {
            if (session.Request is not { } request) return;
            if (request.Unnarratable.Any()) { ctx.Status.Text = "Remove the files that no allowed engine can take before exporting."; return; }
            var folders = await ctx.Dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose where to save the batch request", AllowMultiple = false }).ConfigureAwait(true);
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
            string path = await BatchAdmissionRequestBuilder.WriteAsync(request, folder).ConfigureAwait(true);
            ctx.Status.Text = $"Batch request saved to {path}.\nIt contains hashes and limits, not your text. Installing it once (Install-BatchAuthority.ps1) admits every Long Audio file in it; ordinary-engine files also need their article admission. Then reopen this batch and choose Start.";
        });
        OnBatch(ctx, ctx.Approve, BatchDiagnosticOperation.Approve, async () =>
        {
            if (session.Batch is not { } batch || session.Request is not { } request || ctx.Model.TryCaptureBatchVoice(out _) is not { } voice) return;
            var result = await session.Gate.ApproveInAppAsync(batch, voice.AccountStableId, voice.ProjectStableId, request).ConfigureAwait(true);
            if (!result.Allowed) BatchDiagnostics.Action(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Approve, BatchDiagnosticPhase.Rejected);
            ctx.Status.Text = result.Allowed ? "Approved. Choose Start when ready." : result.Explanation;
            await ReviewBatchAsync(ctx).ConfigureAwait(true);
        });
        ctx.Start.Click += (_, _) =>
        {
            if (session.Running || session.Busy) return;
            var guide = BatchGuide.For(GuideInput(ctx));
            if (guide.StartBlockers.Count > 0) { ctx.Status.Text = guide.Next; return; }
            session.Active = RunBatchAsync(ctx.Dialog, ctx.Model, session, ctx.Controls, () => RefreshBatch(ctx));
            RefreshBatch(ctx);
        };
        ctx.Pause.Click += (_, _) => { BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Pause, BatchDiagnosticPhase.Requested); session.Runner?.RequestPause(); ctx.Status.Text = "Pausing after the current file finishes. Nothing new will start."; };
        ctx.StopNow.Click += (_, _) => { BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Stop, BatchDiagnosticPhase.Requested); session.Run?.Cancel(); ctx.Status.Text = "Stopping at the next safe point. A request already sent is checked, never resent."; };
        ctx.CancelWork.Click += (_, _) => { BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.CancelWork, BatchDiagnosticPhase.Requested); session.Work?.Cancel(); };
        OnBatch(ctx, ctx.Manifest, BatchDiagnosticOperation.WriteManifest, () =>
        {
            if (session.Batch is not { } batch) return Task.CompletedTask;
            var (playlist, csv) = BatchManifestWriter.Write(batch);
            ctx.Status.Text = $"Wrote {System.IO.Path.GetFileName(playlist)} and {System.IO.Path.GetFileName(csv)} in the output folder.";
            return Task.CompletedTask;
        });
        OnBatch(ctx, ctx.OpenFolder, BatchDiagnosticOperation.OpenOutput, async () =>
        {
            if (session.Folder is not { } folder || !Directory.Exists(folder)) { ctx.Status.Text = "The output folder is not available. Choose an output folder."; return; }
            if (!await ctx.Dialog.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)).ConfigureAwait(true))
                ctx.Status.Text = $"The folder could not be opened here. It is {folder}.";
        });
        OnBatch(ctx, ctx.OpenFile, BatchDiagnosticOperation.OpenFile, async () =>
        {
            if (SelectedBatchRows(ctx).FirstOrDefault(x => x.State == BatchItemState.Completed) is not { } done || session.Folder is not { } folder) return;
            string path = done.OutputPath ?? System.IO.Path.Combine(folder, done.OutputFileName);
            if (!File.Exists(path)) { ctx.Status.Text = $"{done.OutputFileName} is not in the output folder any more. Resume the batch to check it; it is never regenerated without your approval."; return; }
            if (!await ctx.Dialog.Launcher.LaunchFileInfoAsync(new FileInfo(path)).ConfigureAwait(true))
                ctx.Status.Text = $"No app opened {done.OutputFileName}. It is in {folder}.";
        });
        ctx.Close.Click += (_, _) => { BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Close, BatchDiagnosticPhase.Requested); ctx.Dialog.Close(); };
        ctx.Dialog.Closing += (_, e) =>
        {
            if (session.Running)
            {
                BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, BatchDiagnosticOperation.Close, BatchDiagnosticPhase.Rejected);
                e.Cancel = true; ctx.Status.Text = "Pause or stop the batch before closing this window.";
                return;
            }
            if (session.Busy)
            {
                // Reading or adding files stops after the current file, so a document is never created without being
                // recorded in the batch. The window closes by itself as soon as that file is done.
                e.Cancel = true;
                ctx.CloseWhenIdle = true;
                session.Work?.Cancel();
                ctx.Status.Text = "Finishing the current file, then closing. Everything done so far is kept.";
            }
        };
    }

    /// <summary>Applies a draft setting at once (before any refresh could reset the control), then saves the draft.</summary>
    private async Task DraftSettingChangedAsync(BatchContext ctx, Action change)
    {
        if (ctx.Refreshing || !ctx.Session.IsDraft) return;
        if (ctx.Session.Busy || ctx.Session.Running)
        {
            RefreshBatch(ctx); // Puts the control back to the draft's value.
            ctx.Status.Text = "Still finishing the previous step. Change the setting again when it is done.";
            return;
        }
        change();
        await GuardBatchAsync(ctx, BatchDiagnosticOperation.ChangeSettings, () => SaveDraftAsync(ctx)).ConfigureAwait(true);
    }

    private static bool BatchOutputExists(BatchSession session, string name) =>
        session.Folder is { } folder && File.Exists(System.IO.Path.Combine(folder, name));

    private static string RenamedText(BatchSession session)
    {
        var renamed = BatchPlanner.Renamed(session.Items, t => t + ".mp3", session.Numbered);
        return renamed.Count == 0 ? string.Empty
            : $" {renamed.Count} name(s) already exist in the output folder or the batch, so a number in brackets was added (for example {renamed[0].OutputFileName}). Nothing is ever overwritten.";
    }

    private static void ReplaceDraft(BatchSession session, IReadOnlyList<BatchItem> items)
    {
        var copy = items.ToArray();
        session.Draft.Clear();
        session.Draft.AddRange(copy);
    }

    private static BatchGuide.Input GuideInput(BatchContext ctx)
    {
        var session = ctx.Session;
        var items = session.Items;
        var voice = ctx.Model.TryCaptureBatchVoice(out string? voiceProblem);
        bool longAudioFiles = session.Batch?.Included.Any(x => string.Equals(x.Engine, BatchEngines.LongAudio, StringComparison.Ordinal) && x.State != BatchItemState.Completed) == true;
        var counts = session.Batch is { } b ? BatchDialogPresentation.Counts(b) : new BatchDialogPresentation.ResultCounts(0, 0, 0, 0, 0, 0);
        return new(items.Count, items.Count(x => x.IsIncluded), voice is not null,
            session.Batch is { } saved && (voice is null || !string.Equals(voice.VoiceStableId, saved.Settings.VoiceStableId, StringComparison.Ordinal))
                ? $"This batch uses {saved.Settings.VoiceLabel}: choose Use this batch's voice" + (ctx.Model.BatchVoiceChoices.Count == 0 ? " (choose Refresh voices first, or connect Google in the main window)." : ".")
                : voice is null && ctx.Model.BatchVoiceChoices.Count == 0 ? "No verified voice yet: choose Refresh voices (or connect Google in the main window). Your draft is kept." : voiceProblem,
            session.Batch is null || voice is not null && string.Equals(voice.VoiceStableId, session.Batch.Settings.VoiceStableId, StringComparison.Ordinal),
            session.Folder is not null, session.Batch is not null, session.Request is not null || session.Decision is { Allowed: true },
            session.Decision is { Allowed: true }, session.Profile?.Mode, session.Profile is not null, session.Request?.Unnarratable.Count() ?? 0,
            longAudioFiles, ctx.Capture.IsChecked == true, session.Running, session.Busy,
            counts.Completed, counts.Failed, counts.Uncertain, counts.Waiting, session.Batch?.State == BatchQueueState.Finished,
            session.Batch?.Items.Count(BatchDocumentImporter.NeedsImport) ?? 0);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Pure enable/visible/text mapping from session state.")]
    private static void RefreshBatch(BatchContext ctx)
    {
        if (ctx.Refreshing) return;
        ctx.Refreshing = true;
        try
        {
            var session = ctx.Session;
            RefreshBatchRows(ctx);
            var selected = SelectedBatchRows(ctx);
            bool running = session.Running, idle = !running && !session.Busy, draft = session.IsDraft;
            var guide = BatchGuide.For(GuideInput(ctx));
            ctx.Steps.Text = guide.StepLine;
            ctx.Next.Text = guide.Next;

            // Voice
            var choices = ctx.Model.BatchVoiceChoices;
            string? selectedVoice = ctx.Model.SelectedVoiceLabVoice?.VoiceStableId;
            ctx.Voice.ItemsSource = choices.Select(v => new ComboBoxItem
            {
                Content = v.VoiceStableId + (v.LanguageCodes is { Count: > 0 } languages ? $"  ({string.Join(", ", languages)})" : string.Empty),
                Tag = v.VoiceStableId,
            }).ToArray();
            ctx.Voice.SelectedIndex = choices.ToList().FindIndex(v => string.Equals(v.VoiceStableId, selectedVoice, StringComparison.Ordinal));
            ctx.Voice.IsEnabled = idle && choices.Count > 0;
            ctx.Voice.PlaceholderText = choices.Count == 0 ? "No verified voices yet — choose Refresh voices" : "Choose the voice for every file…";
            ctx.RefreshVoices.IsEnabled = idle;
            var voice = ctx.Model.TryCaptureBatchVoice(out string? voiceProblem);
            bool voiceMismatch = session.Batch is { } open && (voice is null || !string.Equals(voice.VoiceStableId, open.Settings.VoiceStableId, StringComparison.Ordinal));
            ctx.UseBatchVoice.IsVisible = voiceMismatch;
            ctx.UseBatchVoice.IsEnabled = idle;
            ctx.VoiceText.Text = session.Batch is { } b
                ? $"This batch uses {b.Settings.VoiceLabel} · {BatchEngines.Describe(b.Settings.Engine)} · {(b.Settings.FailurePolicy == BatchFailurePolicy.StopBatch ? "stop on failure" : "skip failures and continue")}."
                  + (voiceMismatch ? " The voice selected now is different; choose Use this batch's voice before reviewing or starting." : string.Empty)
                : voice is null ? (voiceProblem ?? string.Empty) + " You can keep arranging files meanwhile; the draft is saved automatically."
                : $"Every file will use {ShellViewModel.BatchSettingsFor(voice, BatchFailurePolicy.SkipAndContinue).VoiceLabel}.";

            // Output and draft settings
            ctx.OutputText.Text = session.Folder is { } folder ? $"Output folder: {folder}" : "Output folder: not chosen yet (it defaults to the folder of the first file you add).";
            bool canChangeFolder = idle && session.Items.Count > 0 && !session.AnyStarted;
            ctx.ChooseOutput.IsEnabled = canChangeFolder;
            ToolTip.SetTip(ctx.ChooseOutput, canChangeFolder ? "Choose where the MP3 files are saved. Existing files are never overwritten."
                : session.AnyStarted ? "The output folder cannot change after a file has started."
                : session.Busy ? "Wait for the current step to finish." : "Add files first; the folder defaults to the folder of the first file.");
            ctx.Engine.IsVisible = ctx.Policy.IsVisible = ctx.Numbering.IsVisible = draft;
            ctx.Engine.IsEnabled = ctx.Policy.IsEnabled = ctx.Numbering.IsEnabled = draft && idle;
            ctx.Engine.SelectedIndex = session.EngineChoice;
            ctx.Policy.SelectedIndex = session.Policy == BatchFailurePolicy.StopBatch ? 1 : 0;
            ctx.Numbering.IsChecked = session.NumberTracks;

            // Files
            bool canAdd = idle && session.Batch?.State is null or BatchQueueState.Ready or BatchQueueState.Paused;
            ctx.AddFiles.IsEnabled = ctx.AddFolder.IsEnabled = canAdd;
            DragDrop.SetAllowDrop(ctx.DropZone, canAdd);
            ctx.DropHint.IsVisible = canAdd;
            var arrangeable = selected.Where(BatchEditor.CanArrange).ToArray();
            ctx.Up.IsEnabled = idle && arrangeable.Length > 0 && BatchEditor.MoveBy(session.Items, arrangeable.Select(x => x.ItemId).ToArray(), -1) != session.Items;
            ctx.Down.IsEnabled = idle && arrangeable.Length > 0 && BatchEditor.MoveBy(session.Items, arrangeable.Select(x => x.ItemId).ToArray(), 1) != session.Items;
            ctx.Remove.IsEnabled = idle && selected.Any(x => BatchEditor.CanRemove(x, draft));
            string why = selected.Count == 0 ? "Select one or more files first (Shift or Ctrl selects several)." : "Only files that have not started can be moved or removed.";
            if (!ctx.Up.IsEnabled) ToolTip.SetTip(ctx.Up, BatchGuide.Explain(string.Empty, [running ? "The batch is running." : why]));
            else ToolTip.SetTip(ctx.Up, "Moves the selected files up one place (Alt+Up). You can also drag rows.");
            if (!ctx.Down.IsEnabled) ToolTip.SetTip(ctx.Down, BatchGuide.Explain(string.Empty, [running ? "The batch is running." : why]));
            else ToolTip.SetTip(ctx.Down, "Moves the selected files down one place (Alt+Down). You can also drag rows.");
            ctx.UndoButton.IsEnabled = idle && session.Undo.Count > 0;
            ctx.UndoButton.Content = NewDescription(session.Undo.Count > 0 ? $"Undo {session.Undo[^1].What.ToLowerInvariant()}" : "Undo");
            ctx.NewBatch.IsVisible = !draft;
            ctx.NewBatch.IsEnabled = idle;
            ctx.Discard.IsVisible = draft && (session.Draft.Count > 0 || session.OutputFolder is not null);
            ctx.Discard.IsEnabled = idle;
            ctx.Summary.Text = session.Batch is { } shown ? BatchDialogPresentation.Summary(shown, session.Profile, session.ApprovedMinorUnits) : BatchDialogPresentation.DraftSummary(session.Draft);

            // Review, approval and run
            ctx.Save.IsVisible = draft;
            ctx.Save.IsEnabled = idle && draft && guide.SaveBlockers.Count == 0;
            ToolTip.SetTip(ctx.Save, BatchGuide.Explain("Adds each file to the library as its own document and saves the batch. Nothing is sent to Google.", guide.SaveBlockers));
            ctx.Rehearse.IsEnabled = idle && session.Batch is not null;
            var decision = session.Decision;
            ctx.Approve.IsVisible = !draft && session.Profile?.Mode == BatchAuthorityMode.InAppWithinLimits;
            ctx.Approve.IsEnabled = idle && session.Request is not null && decision is { Allowed: false } && !(session.Request?.Unnarratable.Any() ?? false);
            if (session.Request is { } request && session.Profile is { } p)
                ctx.Approve.Content = NewDescription($"Approve {request.ProposedConsent.Items.Count} files, up to {BatchMoney.Format(request.TotalEstimatedMaximumMinorUnits, p)}");
            ctx.Export.IsVisible = !draft && session.Profile?.Mode == BatchAuthorityMode.Reviewed;
            ctx.Export.IsEnabled = idle && session.Request is not null && decision is { Allowed: false };
            bool longAudioFiles = session.Batch?.Included.Any(x => string.Equals(x.Engine, BatchEngines.LongAudio, StringComparison.Ordinal) && x.State != BatchItemState.Completed) == true;
            ctx.Capture.IsVisible = longAudioFiles;
            ctx.Capture.IsEnabled = !running;
            ctx.Start.IsVisible = !draft;
            ctx.Start.IsEnabled = idle && guide.StartBlockers.Count == 0;
            ToolTip.SetTip(ctx.Start, BatchGuide.Explain("Narrates the files one at a time, in the order shown. Finished MP3s are kept if a later file fails.", guide.StartBlockers));
            ctx.Start.Content = NewDescription(session.Batch?.FirstStartedUtc is null ? "Start" : "Resume");
            ctx.Pause.IsVisible = ctx.StopNow.IsVisible = !draft;
            ctx.Pause.IsEnabled = ctx.StopNow.IsEnabled = running;
            ctx.AuthorityText.Text = BatchDialogPresentation.Authority(session.Profile) + (decision is { } d ? "\n" + d.Explanation : string.Empty);
            ctx.ReviewText.Text = session.ReviewText;
            ctx.ReviewText.IsVisible = session.ReviewText.Length > 0;
            ctx.Rehearsal.Text = session.RehearsalText;
            ctx.Rehearsal.IsVisible = session.RehearsalText.Length > 0 && session.Batch is not null;

            // Results
            var item = selected.Count == 1 ? selected[0] : null;
            ctx.Retry.IsEnabled = idle && session.Batch is not null && item?.State == BatchItemState.Failed;
            ctx.CheckAgain.IsEnabled = idle && session.Batch is not null && item?.State == BatchItemState.Uncertain;
            ctx.Skip.IsEnabled = idle && session.Batch is not null && item is { State: BatchItemState.Ready or BatchItemState.Failed or BatchItemState.Uncertain };
            ctx.Manifest.IsEnabled = idle && session.Batch?.CompletedCount > 0;
            ctx.OpenFolder.IsEnabled = session.Folder is { } f && Directory.Exists(f);
            ctx.OpenFile.IsEnabled = selected.Any(x => x.State == BatchItemState.Completed);
            ToolTip.SetTip(ctx.OpenFile, ctx.OpenFile.IsEnabled ? "Opens the selected finished MP3 in your default player." : "Select a finished file (marked done) to play it.");
            ToolTip.SetTip(ctx.Retry, ctx.Retry.IsEnabled ? "Runs the file again through its saved job, so accepted audio is reused. A failed local copy is copied again without new speech." : "Select one failed file to retry it.");
            ToolTip.SetTip(ctx.CheckAgain, ctx.CheckAgain.IsEnabled ? BatchDialogPresentation.CheckAgainGuidance : "Select one file marked needs checking.");
            ctx.Results.Text = session.Batch is { } r ? BatchDialogPresentation.Results(r) : string.Empty;
            ctx.Results.IsVisible = ctx.Results.Text.Length > 0;
            if (session.Batch is { } progressBatch)
            {
                ctx.Timing.Text = BatchDialogPresentation.Timing(progressBatch, session.RunClock.Elapsed);
                ctx.Overall.Value = BatchDialogPresentation.OverallPercentage(progressBatch, session.Current);
            }
            else { ctx.Timing.Text = string.Empty; ctx.Overall.Value = 0; }
            ctx.Close.IsEnabled = !running;
            ctx.Saved.IsEnabled = idle;
        }
        finally { ctx.Refreshing = false; }
    }
}
