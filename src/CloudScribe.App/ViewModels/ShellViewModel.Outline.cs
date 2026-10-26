namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private void RebuildDocumentOutline(string text)
    {
        List<OutlineEntryViewModel> entries = [];
        int position = 0;
        while (position < text.Length)
        {
            int start = position;
            while (position < text.Length && text[position] is not '\r' and not '\n')
            {
                position++;
            }

            int end = position;
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            if (start < end)
            {
                entries.Add(new(CreateOutlineLabel(text, start, end), $"Paragraph {entries.Count + 1}", start, end - start));
            }

            if (position < text.Length && text[position++] == '\r'
                && position < text.Length && text[position] == '\n')
            {
                position++;
            }
        }

        SelectedOutlineIndex = -1;
        for (int index = 0; index < entries.Count; index++)
        {
            if (index == OutlineEntries.Count)
            {
                OutlineEntries.Add(entries[index]);
            }
            else if (OutlineEntries[index] != entries[index])
            {
                OutlineEntries[index] = entries[index];
            }
        }

        while (OutlineEntries.Count > entries.Count)
        {
            OutlineEntries.RemoveAt(OutlineEntries.Count - 1);
        }

        OnPropertyChanged(nameof(OutlineCountLabel));
        OnPropertyChanged(nameof(OutlineHelpText));
    }

    private static string CreateOutlineLabel(string text, int start, int end)
    {
        const int maximumLabelLength = 52;
        int labelLength = Math.Min(end - start, maximumLabelLength);
        if (char.IsHighSurrogate(text[start + labelLength - 1]))
        {
            labelLength--;
        }

        string label = text.Substring(start, labelLength).Replace('\t', ' ');
        return labelLength < end - start ? label + "…" : label;
    }
}
