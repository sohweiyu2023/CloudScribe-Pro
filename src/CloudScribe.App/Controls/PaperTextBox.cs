using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace CloudScribe.App.Controls;

public sealed class PaperTextBox : TextBox
{
    private Border? _paperSurface;

    public PaperTextBox()
    {
        ResourcesChanged += (_, _) => ApplyPaperPalette();
        ActualThemeVariantChanged += (_, _) => ApplyPaperPalette();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _paperSurface = e.NameScope.Find<Border>("PART_PaperSurface");
        ApplyPaperPalette();
    }

    internal void SetVisualCapturePointerOver(bool value) => PseudoClasses.Set(":pointerover", value);

    private void ApplyPaperPalette()
    {
        if (this.TryFindResource("Brush.Paper", out object? paperValue) && paperValue is IBrush paper)
        {
            // Local values outrank Fluent pseudo-class style triggers. Pin both the TextBox
            // and the actual template surface so focus/pointer states cannot expose a dark
            // control background beneath paper-colored document ink.
            Background = paper;
            if (_paperSurface is not null)
            {
                _paperSurface.Background = paper;
            }
        }

        if (this.TryFindResource("Brush.Ink", out object? inkValue) && inkValue is IBrush ink)
        {
            Foreground = ink;
            CaretBrush = ink;
            SelectionForegroundBrush = ink;
        }

        if (this.TryFindResource("Brush.Selection", out object? selectionValue) && selectionValue is IBrush selection)
        {
            SelectionBrush = selection;
        }

        if (this.TryFindResource("Brush.InkMuted", out object? mutedValue) && mutedValue is IBrush muted)
        {
            PlaceholderForeground = muted;
        }
    }
}
