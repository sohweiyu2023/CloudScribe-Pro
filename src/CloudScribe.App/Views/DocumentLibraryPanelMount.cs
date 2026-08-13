using Avalonia.Controls;

namespace CloudScribe.App.Views;

internal static class DocumentLibraryPanelMount
{
    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Border? primaryWorkspace = window.FindControl<Border>("PrimaryWorkspace");
        if (primaryWorkspace?.Child is not Grid productionGrid)
        {
            throw new InvalidOperationException("Primary workspace layout is unavailable for the local document library.");
        }

        Grid? centralWorkspace = productionGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(static child => Grid.GetColumn(child) == 2);
        if (centralWorkspace is null)
        {
            throw new InvalidOperationException("Central workspace layout is unavailable for the local document library.");
        }

        centralWorkspace.Children.Add(new DocumentLibraryPanel
        {
            DataContext = window.DataContext,
        });
    }
}
