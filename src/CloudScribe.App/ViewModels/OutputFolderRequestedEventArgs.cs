namespace CloudScribe.App.ViewModels;

public sealed class OutputFolderRequestedEventArgs : EventArgs
{
    public OutputFolderRequestedEventArgs(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = outputDirectory;
    }

    public string OutputDirectory { get; }
}
