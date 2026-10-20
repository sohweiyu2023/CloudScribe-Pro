using Avalonia.Controls;

namespace CloudScribe.App.Composition;

internal static class DialogBounds
{
    public static void Fit(Window dialog, Window owner)
    {
        if (owner.Screens.ScreenFromWindow(owner) is not { } screen || screen.Scaling <= 0) return;
        double width = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 24);
        double height = Math.Max(320, screen.WorkingArea.Height / screen.Scaling - 24);
        dialog.MinWidth = Math.Min(dialog.MinWidth, width);
        dialog.MinHeight = Math.Min(dialog.MinHeight, height);
        dialog.Width = Math.Min(dialog.Width, width);
        dialog.Height = Math.Min(dialog.Height, height);
    }
}
