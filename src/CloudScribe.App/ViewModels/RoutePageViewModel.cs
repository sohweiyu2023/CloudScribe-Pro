using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudScribe.App.ViewModels;

public partial class RoutePageViewModel(
    string title,
    string eyebrow,
    string description,
    string stateTitle,
    string stateDescription,
    string stateKind) : ObservableObject
{
    public string Title { get; } = title;

    public string Eyebrow { get; } = eyebrow;

    public string Description { get; } = description;

    private string _stateTitle = stateTitle;
    private string _stateDescription = stateDescription;
    private string _stateKind = stateKind;

    public string StateTitle
    {
        get => _stateTitle;
        set => SetProperty(ref _stateTitle, value);
    }

    public string StateDescription
    {
        get => _stateDescription;
        set => SetProperty(ref _stateDescription, value);
    }

    public string StateKind
    {
        get => _stateKind;
        set => SetProperty(ref _stateKind, value);
    }

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _hasPrimaryAction;

    [ObservableProperty]
    private string _primaryActionLabel = string.Empty;

    [ObservableProperty]
    private ICommand? _primaryActionCommand;
}
