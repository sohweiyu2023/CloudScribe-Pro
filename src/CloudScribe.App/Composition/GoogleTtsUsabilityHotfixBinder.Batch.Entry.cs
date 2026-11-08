using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private void MountQueueBatchEntries(MainWindow owner, ShellViewModel model)
    {
        Mount("RouteStateSurface", "QueuePageDropZone", "QueuePageBatchButton", () => model.IsQueueSelected);
        Mount("QueueDrawerSurface", "QueueDrawerDropZone", "QueueDrawerBatchButton", () => model.IsQueueDrawerOpen);

        void Mount(string surfaceName, string zoneName, string buttonName, Func<bool> visible)
        {
            var surface = owner.FindControl<Control>(surfaceName);
            var zone = owner.FindControl<Border>(zoneName);
            var button = owner.FindControl<Button>(buttonName);
            if (surface is null || zone is null || button is null) return;
            button.Click += async (_, _) => await OpenFromQueueAsync(null).ConfigureAwait(true);
            DragDrop.AddDragEnterHandler(surface, (_, e) => UpdateDrop(e));
            DragDrop.AddDragOverHandler(surface, (_, e) => UpdateDrop(e));
            DragDrop.AddDragLeaveHandler(surface, (_, _) => Highlight(false));
            DragDrop.AddDropHandler(surface, (_, e) =>
            {
                if (!visible()) return;
                Highlight(false);
                // Capture storage items during the event, before the dialog restores its saved draft.
                var items = e.DataTransfer.TryGetFiles();
                e.Handled = true;
                if (items is null || items.Length == 0)
                {
                    model.StatusMessage = "Drop TXT files or folders from File Explorer, or choose Batch MP3 to browse.";
                    return;
                }
                _ = OpenFromQueueAsync(items);
            });

            void UpdateDrop(DragEventArgs e)
            {
                if (!visible()) return;
                bool allowed = !_batchDialogOpen && e.DataTransfer.Contains(DataFormat.File);
                e.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
                Highlight(allowed);
                e.Handled = true;
            }

            void Highlight(bool active) => zone.BorderThickness = new Thickness(active ? 4 : 2);

            async Task OpenFromQueueAsync(IStorageItem[]? items)
            {
                if (!visible()) return;
                if (_batchDialogOpen)
                {
                    BatchDiagnostics.Action(catalogLogger, null, null, items is null ? BatchDiagnosticOperation.Window : BatchDiagnosticOperation.Drop, BatchDiagnosticPhase.Rejected);
                    model.StatusMessage = "The batch window is already opening or open. Add files there when it is ready.";
                    return;
                }
                model.CloseTransientSurfacesCommand.Execute(null);
                await OpenBatchAsync(owner, model, items).ConfigureAwait(true);
            }
        }
    }
}
