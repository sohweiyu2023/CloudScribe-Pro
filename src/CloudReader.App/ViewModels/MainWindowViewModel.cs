using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudReader.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "CloudScribe Pro";
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private string _selectedMode = "Mode A - Local Chunked";
}
