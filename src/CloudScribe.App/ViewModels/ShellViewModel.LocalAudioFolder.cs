using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    [RelayCommand]
    private void OpenSavedAudioFolder()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudScribe Pro", "Generated");
        if (!Directory.Exists(folder))
        {
            StatusMessage = "No generated MP3 folder exists yet. Audio generation has not been started.";
            return;
        }
        StatusMessage = "Opening saved MP3 folder for local playback. No provider request was sent.";
        OutputFolderRequested?.Invoke(this, new OutputFolderRequestedEventArgs(folder));
    }
}
