using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private const double DragStartDistance = 6;
    private const double AutoScrollEdge = 28;

    /// <summary>A row drag in progress: which files move, where the pointer started and where they would land.</summary>
    private sealed class BatchRowDrag
    {
        public required Point Start { get; init; }
        public required Guid[] Ids { get; init; }
        public required Guid PressedId { get; init; }
        public bool Active { get; set; }
        public int InsertIndex { get; set; } = -1;
        public double PointerY { get; set; }
        public Avalonia.Threading.DispatcherTimer? Scroller { get; set; }
    }

    private static readonly FuncDataTemplate<BatchRowModel> BatchRowTemplate = new((row, _) => row is null ? new TextBlock() : BatchRowView(row));

    private static Border BatchRowView(BatchRowModel row)
    {
        var item = row.Item;
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
        string detail = row.Text + (item.NarrationSource is { } source ? $"\n{source.OmittedUrlCount} URL(s) skipped in a separate narration copy; original retained." : string.Empty);
        text.Children.Add(new TextBlock { Text = detail, Foreground = CosmicDialogTheme.Solid("#C6BCCF"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var grip = new TextBlock
        {
            Text = BatchEditor.CanArrange(item) ? "⠿" : " ",
            Foreground = CosmicDialogTheme.Solid("#8C819A"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 16,
        };
        if (BatchEditor.CanArrange(item)) ToolTip.SetTip(grip, "Drag to change the order");
        var dock = new DockPanel();
        DockPanel.SetDock(grip, Dock.Left);
        dock.Children.Add(grip);
        dock.Children.Add(text);
        var border = new Border { BorderBrush = CosmicDialogTheme.Solid(accent), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 4), Child = dock };
        AutomationProperties.SetName(border, (BatchDialogPresentation.Title(item) + ". " + detail).Replace("\n", ". ", StringComparison.Ordinal));
        return border;
    }

    /// <summary>Rebuilds the rows only when their text changes, and keeps the user's selection and focus.</summary>
    private static void RefreshBatchRows(BatchContext ctx)
    {
        var items = ctx.Session.Items;
        var profile = ctx.Session.Profile;
        var byOrder = items.GroupBy(x => x.Order).ToDictionary(g => g.Key, g => g.First());
        var rows = items.Select(x =>
        {
            string? earlierName = x.SameTextAs is { } earlier && byOrder.TryGetValue(earlier, out var first) ? first.SourceFileName : null;
            return new BatchRowModel(x, BatchDialogPresentation.Detail(x, profile, earlierName), earlierName);
        }).ToArray();
        string key = string.Join('\u001e', rows.Select(x => x.Item.ItemId + "\u001f" + BatchDialogPresentation.Title(x.Item) + "\u001f" + x.Text + "\u001f" + x.EarlierName));
        if (string.Equals(key, ctx.ShownRows, StringComparison.Ordinal)) return;
        var selected = SelectedBatchRows(ctx).Select(x => x.ItemId).ToHashSet();
        bool hadFocus = ctx.List.IsKeyboardFocusWithin;
        ctx.ShownRows = key;
        ctx.List.ItemTemplate = BatchRowTemplate;
        ctx.List.ItemsSource = rows;
        ctx.EmptyHint.IsVisible = rows.Length == 0;
        foreach (var row in rows.Where(x => selected.Contains(x.Item.ItemId))) ctx.List.SelectedItems?.Add(row);
        if (hadFocus) RestoreBatchListFocus(ctx);
    }

    /// <summary>After the rows are rebuilt, keyboard focus goes back to the (first) selected row, so a keyboard user can
    /// keep moving the same files with Alt+Up / Alt+Down and a screen reader stays on them.</summary>
    private static void RestoreBatchListFocus(BatchContext ctx) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var target = ctx.List.SelectedItems?.OfType<BatchRowModel>().FirstOrDefault();
            if (target is not null && ctx.List.ContainerFromItem(target) is Control container) container.Focus(NavigationMethod.Directional);
            else ctx.List.Focus();
        }, Avalonia.Threading.DispatcherPriority.Background);

    private static List<BatchItem> SelectedBatchRows(BatchContext ctx)
    {
        var selected = ctx.List.SelectedItems?.OfType<BatchRowModel>().Select(x => x.Item.ItemId).ToHashSet() ?? [];
        return ctx.Session.Items.Where(x => selected.Contains(x.ItemId)).ToList();
    }

    private static void SelectBatchItems(BatchContext ctx, IReadOnlyCollection<Guid> ids)
    {
        RefreshBatchRows(ctx);
        if (ctx.List.ItemsSource is not BatchRowModel[] rows) return;
        ctx.List.SelectedItems?.Clear();
        BatchRowModel? first = null;
        foreach (var row in rows.Where(x => ids.Contains(x.Item.ItemId))) { ctx.List.SelectedItems?.Add(row); first ??= row; }
        if (first is not null) ctx.List.ScrollIntoView(first);
    }

    private static void PushBatchUndo(BatchSession session, IReadOnlyList<BatchItem> snapshot, string what) =>
        session.Undo.Add(new BatchUndo(snapshot, what));

    /// <summary>Applies one arrangement change to the selected files, renames, saves (draft or batch) and keeps them selected.</summary>
    private async Task EditBatchAsync(BatchContext ctx, string what, Func<IReadOnlyList<BatchItem>, IReadOnlyCollection<Guid>, IReadOnlyList<BatchItem>> change, Guid[]? ids = null)
    {
        var session = ctx.Session;
        ids ??= [.. SelectedBatchRows(ctx).Select(x => x.ItemId)];
        if (ids.Length == 0) { ctx.Status.Text = "Select one or more files first (Shift or Ctrl selects several)."; return; }
        var before = session.Items.ToArray();
        var changed = change(before, ids);
        if (ReferenceEquals(changed, before) || changed.Select(x => (x.ItemId, x.State)).SequenceEqual(before.Select(x => (x.ItemId, x.State))))
        { ctx.Status.Text = "Nothing to change: only files that have not started can be moved or removed, and they are already there."; return; }
        await SaveArrangementAsync(ctx, changed, () => PushBatchUndo(session, before, what)).ConfigureAwait(true);
        if (!session.IsDraft) PushBatchUndo(session, before, what);
        BatchDiagnostics.Action(catalogLogger, session.Batch?.BatchId, null, string.Equals(what, "Removed", StringComparison.Ordinal) ? BatchDiagnosticOperation.Remove : BatchDiagnosticOperation.Reorder, BatchDiagnosticPhase.Returned);
        SelectBatchItems(ctx, ids);
        int count = ids.Length;
        ctx.Status.Text = $"{what} {count} file(s). Undo (Ctrl+Z) puts them back." + RenamedText(session)
            + (session.Batch is not null && string.Equals(what, "Removed", StringComparison.Ordinal) ? " A changed batch is reviewed again before it starts." : string.Empty);
    }

    private async Task SaveArrangementAsync(BatchContext ctx, IReadOnlyList<BatchItem> changed, Action? draftApplied = null)
    {
        var session = ctx.Session;
        var renamed = BatchPlanner.AssignOutputNames(changed, t => t + ".mp3", n => BatchOutputExists(session, n), session.Numbered);
        if (session.Batch is { } batch)
        {
            session.Batch = await session.Store.SaveAsync(batch with { Items = renamed }).ConfigureAwait(true);
            await ReviewBatchAsync(ctx).ConfigureAwait(true);
        }
        else
        {
            ReplaceDraft(session, renamed);
            // In-memory edits remain applied if autosave fails, so their undo snapshot must remain too.
            draftApplied?.Invoke();
            await SaveDraftAsync(ctx).ConfigureAwait(true);
        }
    }

    private async Task UndoBatchAsync(BatchContext ctx)
    {
        var session = ctx.Session;
        if (session.Undo.Count == 0) return;
        var step = session.Undo[^1];
        session.Undo.RemoveAt(session.Undo.Count - 1);
        if (step.DraftSettings is { } settings && session.IsDraft) ApplyDraftSettings(session, settings);
        var restored = BatchEditor.Restore(session.Items, step.Items, keepAdded: !session.IsDraft);
        await SaveArrangementAsync(ctx, restored).ConfigureAwait(true);
        await LoadSavedListAsync(ctx).ConfigureAwait(true);
        ctx.Status.Text = $"Undid {step.What.ToLowerInvariant()}." + (session.Undo.Count > 0 ? $" {session.Undo.Count} more step(s) can be undone." : string.Empty);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Pointer and keyboard handling for one list.")]
    private void WireBatchRows(BatchContext ctx)
    {
        var list = ctx.List;
        list.SelectionChanged += (_, _) => { if (!ctx.Refreshing) RefreshBatch(ctx); };

        // Keyboard alternatives for everything the mouse can do.
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (ctx.Drag is { Active: true } && e.Key == Key.Escape) { EndRowDrag(ctx, cancelled: true); e.Handled = true; return; }
            Func<Task>? action = (e.Key, e.KeyModifiers) switch
            {
                (Key.Up, KeyModifiers.Alt) => () => EditBatchAsync(ctx, "Moved up", (items, ids) => BatchEditor.MoveBy(items, ids, -1)),
                (Key.Down, KeyModifiers.Alt) => () => EditBatchAsync(ctx, "Moved down", (items, ids) => BatchEditor.MoveBy(items, ids, 1)),
                (Key.Home, KeyModifiers.Alt) => () => EditBatchAsync(ctx, "Moved to the top", (items, ids) => BatchEditor.MoveTo(items, ids, 0)),
                (Key.End, KeyModifiers.Alt) => () => EditBatchAsync(ctx, "Moved to the end", (items, ids) => BatchEditor.MoveTo(items, ids, items.Count)),
                (Key.Delete, KeyModifiers.None) => () => EditBatchAsync(ctx, "Removed", (items, ids) => BatchEditor.Remove(items, ids, ctx.Session.IsDraft)),
                (Key.Z, KeyModifiers.Control) => () => UndoBatchAsync(ctx),
                (Key.Enter, KeyModifiers.None) when SelectedBatchRows(ctx).Any(x => x.State == BatchItemState.Completed) => () => { ctx.OpenFile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; }
                ,
                _ => null,
            };
            if (action is null) return;
            e.Handled = true;
            _ = GuardBatchAsync(ctx, e.Key == Key.Z ? BatchDiagnosticOperation.Undo : e.Key == Key.Delete ? BatchDiagnosticOperation.Remove : BatchDiagnosticOperation.Reorder, action);
        }, RoutingStrategies.Tunnel);

        // Dragging rows: press, move past a few pixels, and an insertion line shows where the files will land.
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            ctx.Drag = null;
            if (ctx.Session.Running || ctx.Session.Busy || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            if (RowAt(e.Source) is not { } row || !BatchEditor.CanArrange(row.Item)) return;
            var selected = SelectedBatchRows(ctx);
            bool inSelection = selected.Any(x => x.ItemId == row.Item.ItemId);
            var ids = inSelection && selected.Count > 1 ? selected.Where(BatchEditor.CanArrange).Select(x => x.ItemId).ToArray() : [row.Item.ItemId];
            ctx.Drag = new BatchRowDrag { Start = e.GetPosition(list), Ids = ids, PressedId = row.Item.ItemId };
            // Keep a multi-file selection when pressing one of its rows, so the whole group can be dragged.
            if (inSelection && selected.Count > 1 && e.KeyModifiers == KeyModifiers.None) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (ctx.Drag is not { } drag) return;
            var position = e.GetPosition(list);
            if (!drag.Active)
            {
                if (Math.Abs(position.Y - drag.Start.Y) < DragStartDistance && Math.Abs(position.X - drag.Start.X) < DragStartDistance) return;
                drag.Active = true;
                e.Pointer.Capture(list);
                drag.Scroller = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                drag.Scroller.Tick += (_, _) => AutoScroll(ctx);
                drag.Scroller.Start();
                ctx.Status.Text = drag.Ids.Length == 1 ? "Drag to the new place and release. Esc cancels." : $"Moving {drag.Ids.Length} files together. Release at the new place; Esc cancels.";
            }
            drag.PointerY = position.Y;
            UpdateInsertion(ctx, position.Y);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (ctx.Drag is not { } drag) return;
            if (!drag.Active)
            {
                // A plain click on a row of a multi-file selection selects just that row, as usual.
                ctx.Drag = null;
                if (e.KeyModifiers == KeyModifiers.None && SelectedBatchRows(ctx).Count > 1) SelectBatchItems(ctx, [drag.PressedId]);
                return;
            }
            e.Pointer.Capture(null);
            e.Handled = true;
            int index = drag.InsertIndex;
            EndRowDrag(ctx, cancelled: false);
            if (index < 0) return;
            _ = GuardBatchAsync(ctx, BatchDiagnosticOperation.DragReorder,
                () => EditBatchAsync(ctx, "Moved", (items, ids) => BatchEditor.MoveTo(items, ids, index), drag.Ids));
        }, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { if (ctx.Drag is { Active: true }) EndRowDrag(ctx, cancelled: true); });

        static BatchRowModel? RowAt(object? source) =>
            (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as BatchRowModel;
    }

    /// <summary>Finds the gap under the pointer among the visible rows and draws the insertion line there.</summary>
    private static void UpdateInsertion(BatchContext ctx, double y)
    {
        var list = ctx.List;
        int count = ctx.Session.Items.Count;
        int index = count;
        double lineY = double.NaN;
        Rect? last = null;
        for (int i = 0; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not Control container || !container.IsVisible) continue;
            if (container.TranslatePoint(new Point(0, 0), list) is not { } top) continue;
            var bounds = new Rect(top, container.Bounds.Size);
            if (bounds.Bottom < 0 || bounds.Top > list.Bounds.Height) { if (bounds.Bottom < 0) index = i + 1; continue; }
            if (y < bounds.Top + bounds.Height / 2) { index = i; lineY = bounds.Top; break; }
            index = i + 1; lineY = bounds.Bottom; last = bounds;
        }
        if (double.IsNaN(lineY)) lineY = last?.Bottom ?? 4;
        ctx.Drag!.InsertIndex = index;
        ctx.Indicator.Width = Math.Max(0, list.Bounds.Width - 16);
        Canvas.SetLeft(ctx.Indicator, 8);
        Canvas.SetTop(ctx.Indicator, Math.Clamp(lineY - 1.5, 0, Math.Max(0, list.Bounds.Height - 3)));
        ctx.Indicator.IsVisible = true;
        AutomationProperties.SetName(ctx.Indicator, string.Create(CultureInfo.InvariantCulture, $"Insert before position {index + 1}"));
    }

    private static void AutoScroll(BatchContext ctx)
    {
        if (ctx.Drag is not { Active: true } drag) return;
        var viewer = ctx.List.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null) return;
        double height = ctx.List.Bounds.Height, step = 0;
        if (drag.PointerY < AutoScrollEdge) step = -Math.Max(4, AutoScrollEdge - drag.PointerY);
        else if (drag.PointerY > height - AutoScrollEdge) step = Math.Max(4, drag.PointerY - (height - AutoScrollEdge));
        if (step == 0) return;
        viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(viewer.Offset.Y + step, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
        UpdateInsertion(ctx, drag.PointerY);
    }

    private static void EndRowDrag(BatchContext ctx, bool cancelled)
    {
        if (ctx.Drag is { } drag) { drag.Scroller?.Stop(); drag.Scroller = null; }
        ctx.Drag = null;
        ctx.Indicator.IsVisible = false;
        if (cancelled) ctx.Status.Text = "Move cancelled; the order is unchanged.";
    }
}
