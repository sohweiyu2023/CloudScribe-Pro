using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static StackPanel AddSection(StackPanel host, string title, bool expanded)
    {
        StackPanel content = new() { Spacing = 10, Margin = new Thickness(0, 12, 0, 4) };
        Expander section = new()
        {
            Header = NewDescription(title),
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Classes = { "voice-step" },
        };
        AutomationProperties.SetName(section, title);
        host.Children.Add(section);
        return content;
    }

    private static TextBlock NewDescription(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.Normal,
    };

    private static Button NewButton(string label)
    {
        Button button = new()
        {
            Content = NewDescription(label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static CheckBox NewConfirmation(string label)
    {
        CheckBox confirmation = new() { Content = NewDescription(label) };
        AutomationProperties.SetName(confirmation, label);
        return confirmation;
    }

    private static void AddField(StackPanel host, string label, Control input)
    {
        TextBlock caption = NewDescription(label);
        caption.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetName(input, label);
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children = { caption, input },
        });
    }

    private static void AddLiveStatus(StackPanel host, ShellViewModel viewModel)
    {
        TextBlock status = NewDescription(viewModel.StatusMessage);
        AutomationProperties.SetName(status, "Studio status");
        viewModel.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.StatusMessage), StringComparison.Ordinal))
                status.Text = viewModel.StatusMessage;
        };
        host.Children.Add(status);
    }
}
