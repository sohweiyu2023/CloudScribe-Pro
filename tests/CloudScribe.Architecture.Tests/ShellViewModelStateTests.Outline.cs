using CloudScribe.App.ViewModels;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public void OutlineFollowsCurrentTextInsteadOfReleasePrimer()
    {
        using ShellViewModel model = CreateViewModel();
        model.ApplyFinalReleasePresentation();
        model.DocumentText = "My actual document.";

        OutlineEntryViewModel entry = Assert.Single(model.OutlineEntries);
        Assert.Equal("My actual document.", entry.Label);
        Assert.Equal("Paragraph 1", entry.RangeLabel);
        Assert.Equal("1 paragraph", model.OutlineCountLabel);

        model.DocumentText = " \r\n\t\n";
        Assert.Empty(model.OutlineEntries);
        Assert.Contains("Add text", model.OutlineHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void OutlineKeepsExactOffsetsAcrossBlankLinesAndDuplicateParagraphs()
    {
        using ShellViewModel model = CreateViewModel();
        const string text = "  First paragraph.\r\n\r\nRepeated paragraph.\n \nRepeated paragraph.\rFinal paragraph.";
        model.DocumentText = text;
        List<OutlineNavigationRequestedEventArgs> requests = [];
        model.OutlineNavigationRequested += (_, args) => requests.Add(args);

        Assert.Equal(4, model.OutlineEntries.Count);
        model.SelectedOutlineIndex = 2;
        model.SelectedOutlineIndex = 2;

        Assert.Equal(2, requests.Count);
        foreach (OutlineNavigationRequestedEventArgs request in requests)
        {
            Assert.Equal(text.LastIndexOf("Repeated paragraph.", StringComparison.Ordinal), request.StartOffset);
            Assert.Equal("Repeated paragraph.", text.Substring(request.StartOffset, request.SelectionLength));
        }

        Assert.Equal(text, model.DocumentText);
        Assert.Equal(-1, model.SelectedOutlineIndex);
        Assert.Equal("Paragraph 3 highlighted in your text", model.StatusMessage);
    }

    [Fact]
    public void EditingRebuildsDestinationsWithoutLeavingOldAnchors()
    {
        using ShellViewModel model = CreateViewModel();
        model.DocumentText = "Original.\nTarget.";
        model.DocumentText = "New first paragraph.\nOriginal.\nTarget changed.";
        OutlineNavigationRequestedEventArgs? request = null;
        model.OutlineNavigationRequested += (_, args) => request = args;

        model.SelectedOutlineIndex = 2;

        Assert.NotNull(request);
        Assert.Equal("Target changed.", model.DocumentText.Substring(request.StartOffset, request.SelectionLength));
        Assert.Equal("3 paragraphs", model.OutlineCountLabel);
    }

    [Fact]
    public void LongDocumentNavigationSelectsWholeDistantParagraph()
    {
        using ShellViewModel model = CreateViewModel();
        string[] paragraphs = Enumerable.Range(1, 80)
            .Select(index => $"Paragraph {index}: " + new string('x', 200))
            .ToArray();
        model.DocumentText = string.Join("\r\n\r\n", paragraphs);
        OutlineNavigationRequestedEventArgs? request = null;
        model.OutlineNavigationRequested += (_, args) => request = args;

        model.SelectedOutlineIndex = 79;

        Assert.NotNull(request);
        Assert.True(request.StartOffset > 10_000);
        Assert.Equal(paragraphs[79], model.DocumentText.Substring(request.StartOffset, request.SelectionLength));
        Assert.True(model.OutlineEntries[79].Label.Length <= 53);
    }

    [Fact]
    public void ShortOutlineLabelDoesNotSplitUnicodeSurrogatePair()
    {
        using ShellViewModel model = CreateViewModel();
        model.DocumentText = new string('a', 51) + "😀 tail";

        string label = Assert.Single(model.OutlineEntries).Label;

        Assert.Equal(new string('a', 51) + "…", label);
    }
}
