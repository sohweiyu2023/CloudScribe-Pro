namespace CloudScribe.App.ViewModels;

public partial class RoutePageViewModel
{
    public bool IsDocumentLibraryRoute =>
        string.Equals(Title, "Library", StringComparison.Ordinal);
}
