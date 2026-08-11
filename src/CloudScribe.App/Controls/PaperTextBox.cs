using Avalonia.Controls;

namespace CloudScribe.App.Controls;

public sealed class PaperTextBox : TextBox
{
    internal void SetVisualCapturePointerOver(bool value) => PseudoClasses.Set(":pointerover", value);
}
