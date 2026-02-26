using CloudReader.Core.Models;
using CloudReader.Core.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudReader.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly TextSanitizer _sanitizer = new();

    [ObservableProperty] private string _title = "CloudScribe Pro";
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private string _selectedMode = "Mode A - Local Chunked";

    [ObservableProperty] private bool _skipUrls;
    [ObservableProperty] private bool _skipRoundBrackets;
    [ObservableProperty] private bool _skipSquareBrackets;
    [ObservableProperty] private bool _skipCurlyBrackets;
    [ObservableProperty] private bool _excludeInsideSsmlTags = true;

    [ObservableProperty] private string _sanitizedPreview = string.Empty;
    [ObservableProperty] private int _removedSpanCount;
    [ObservableProperty] private int _charactersRemoved;

    [RelayCommand]
    private void PreviewSanitizedText()
    {
        var options = new PreprocessOptions
        {
            SkipUrls = SkipUrls,
            SkipRoundBrackets = SkipRoundBrackets,
            SkipSquareBrackets = SkipSquareBrackets,
            SkipCurlyBrackets = SkipCurlyBrackets,
            ExcludeInsideSsmlTags = ExcludeInsideSsmlTags
        };

        var result = _sanitizer.SanitizeWithReport(EditorText, options);
        SanitizedPreview = result.SanitizedText;
        RemovedSpanCount = result.RemovedSpanCount;
        CharactersRemoved = result.CharactersRemoved;
    }
}
