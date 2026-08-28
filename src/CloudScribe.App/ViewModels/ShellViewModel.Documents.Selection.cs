using CloudScribe.Application.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    [ObservableProperty]
    private DocumentSummary? _selectedLocalDocument;

    partial void OnSelectedLocalDocumentChanged(DocumentSummary? value)
    {
        if (value is null)
        {
            return;
        }

        OpenDocumentCommand.Execute(value.Id);
        SelectedLocalDocument = null;
    }
}
