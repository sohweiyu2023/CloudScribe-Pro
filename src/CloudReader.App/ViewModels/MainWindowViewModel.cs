using System.Collections.ObjectModel;
using CloudReader.Core.Models;
using CloudReader.Core.Processing;
using CloudReader.GoogleTts.Models;
using CloudReader.GoogleTts.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudReader.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly TextSanitizer _sanitizer = new();
    private readonly IGoogleTtsClient _googleTtsClient;
    private readonly List<VoiceCatalogItem> _allVoices = [];

    public MainWindowViewModel(IGoogleTtsClient googleTtsClient)
    {
        _googleTtsClient = googleTtsClient;
    }

    [ObservableProperty] private string _title = "CloudScribe Pro";
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private string _selectedMode = "Mode A - Local Chunked";
    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private bool _skipUrls;
    [ObservableProperty] private bool _skipRoundBrackets;
    [ObservableProperty] private bool _skipSquareBrackets;
    [ObservableProperty] private bool _skipCurlyBrackets;
    [ObservableProperty] private bool _excludeInsideSsmlTags = true;

    [ObservableProperty] private string _sanitizedPreview = string.Empty;
    [ObservableProperty] private int _removedSpanCount;
    [ObservableProperty] private int _charactersRemoved;

    [ObservableProperty] private string _voiceSearch = string.Empty;
    [ObservableProperty] private string _selectedTier = "All";
    [ObservableProperty] private string _selectedGender = "All";
    [ObservableProperty] private string _selectedLanguage = "All";
    [ObservableProperty] private VoiceCatalogItem? _selectedVoice;

    public ObservableCollection<VoiceCatalogItem> Voices { get; } = [];
    public IReadOnlyList<string> ModeOptions { get; } = ["Mode A - Local Chunked", "Mode B - Long Audio (GCS)"];
    public IReadOnlyList<string> TierOptions { get; } = ["All", "Standard", "WaveNet", "Neural2", "Chirp3-HD", "Studio", "Unknown"];
    public IReadOnlyList<string> GenderOptions { get; } = ["All", "Male", "Female", "Neutral", "SsmlVoiceGenderUnspecified"];
    public ObservableCollection<string> LanguageOptions { get; } = ["All"];

    partial void OnVoiceSearchChanged(string value) => ApplyVoiceFilters();
    partial void OnSelectedTierChanged(string value) => ApplyVoiceFilters();
    partial void OnSelectedGenderChanged(string value) => ApplyVoiceFilters();
    partial void OnSelectedLanguageChanged(string value) => ApplyVoiceFilters();

    [RelayCommand]
    private async Task LoadVoicesAsync()
    {
        try
        {
            StatusMessage = "Loading voices from Google Cloud...";
            var voices = await _googleTtsClient.ListVoicesAsync(CancellationToken.None);
            _allVoices.Clear();
            _allVoices.AddRange(voices.OrderBy(static v => v.Name, StringComparer.OrdinalIgnoreCase));

            var langs = _allVoices.SelectMany(static v => v.LanguageCodes).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static l => l).ToList();
            LanguageOptions.Clear();
            LanguageOptions.Add("All");
            foreach (var lang in langs) LanguageOptions.Add(lang);

            ApplyVoiceFilters();
            StatusMessage = $"Loaded {_allVoices.Count} voices.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load voices. Ensure ADC or service-account credentials are configured. {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (SelectedVoice is null)
        {
            StatusMessage = "Select a voice first.";
            return;
        }

        try
        {
            StatusMessage = $"Generating preview for {SelectedVoice.Name}...";
            var request = new TtsRequest(
                "This is CloudScribe voice preview.",
                SelectedVoice.Name,
                SelectedVoice.LanguageCodes.FirstOrDefault() ?? "en-US",
                "Mp3",
                1.0,
                0,
                0,
                null,
                false);

            var audio = await _googleTtsClient.SynthesizeAsync(request, CancellationToken.None);
            StatusMessage = $"Preview generated ({audio.Length} bytes). Playback wiring is next slice.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview failed: {ex.Message}";
        }
    }

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

    [RelayCommand]
    private void ImportDocument() => StatusMessage = "Import workflow is scheduled for next slice (txt/md/html/docx).";

    [RelayCommand]
    private void NewDocument()
    {
        EditorText = string.Empty;
        SanitizedPreview = string.Empty;
        StatusMessage = "New document started.";
    }

    [RelayCommand]
    private void GenerateAudio() => StatusMessage = "Generation pipeline wiring is scheduled for next slice.";

    private void ApplyVoiceFilters()
    {
        var filtered = _allVoices.Where(v =>
            (string.IsNullOrWhiteSpace(VoiceSearch) || v.Name.Contains(VoiceSearch, StringComparison.OrdinalIgnoreCase)) &&
            (SelectedTier == "All" || v.Tier.Equals(SelectedTier, StringComparison.OrdinalIgnoreCase)) &&
            (SelectedGender == "All" || v.SsmlGender.Equals(SelectedGender, StringComparison.OrdinalIgnoreCase)) &&
            (SelectedLanguage == "All" || v.LanguageCodes.Contains(SelectedLanguage, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        Voices.Clear();
        foreach (var item in filtered)
        {
            Voices.Add(item);
        }
    }
}
