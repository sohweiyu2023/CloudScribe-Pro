using CloudScribe.Application.Documents;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed class LibraryPartViewModel
{
    public LibraryPartViewModel(NarrationLibraryPart part, IAsyncRelayCommand<Guid> open)
    {
        Document = part.Document;
        OpenCommand = open;
        Status = part.ChangedSincePreparation ? $"Part {part.Ordinal} · changed since preparation" : $"Part {part.Ordinal} · {part.Document.Status}";
    }
    public DocumentSummary Document { get; }
    public string Status { get; }
    public IAsyncRelayCommand<Guid> OpenCommand { get; }
}
