using CloudScribe.Application.Documents;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed class LibraryArticleViewModel
{
    public LibraryArticleViewModel(NarrationLibraryArticle article, IAsyncRelayCommand<Guid> open, bool expandParts = false)
    {
        Document = article.Document;
        ExpandParts = expandParts;
        OpenCommand = open;
        Parts = article.Parts.Select(part => new LibraryPartViewModel(part, open)).ToArray();
        PreparationStatus = article.ChangedSincePreparation
            ? "Changed since preparation — review current text before any synthesis."
            : "Grouping is local organization only; every part still needs its own request review.";
    }
    public DocumentSummary Document { get; }
    public IAsyncRelayCommand<Guid> OpenCommand { get; }
    public IReadOnlyList<LibraryPartViewModel> Parts { get; }
    public bool HasParts => Parts.Count > 0;
    public bool ExpandParts { get; }
    public string PartsLabel => $"Technical narration parts ({Parts.Count})";
    public string PreparationStatus { get; }
}

