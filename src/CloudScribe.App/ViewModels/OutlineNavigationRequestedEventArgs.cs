namespace CloudScribe.App.ViewModels;

public sealed class OutlineNavigationRequestedEventArgs(int startOffset, int selectionLength) : EventArgs
{
    public int StartOffset { get; } = startOffset;

    public int SelectionLength { get; } = selectionLength;
}
