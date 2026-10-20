namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task<int> CreateLocalNarrationPartsAsync(IReadOnlyList<string> parts, string originalText)
    {
        EnsureDocumentWorkflowConfigured();
        if (!HasOpenDocument || !string.Equals(DocumentText, originalText, StringComparison.Ordinal))
            throw new InvalidOperationException("The source changed. Preview narration parts again.");
        if (parts.Count is < 1 or > 100 || !string.Equals(string.Concat(parts), originalText, StringComparison.Ordinal))
            throw new InvalidOperationException("Narration parts must preserve the exact complete source text.");
        if (!await SaveCurrentDocumentCoreAsync("Before creating local narration parts").ConfigureAwait(true))
            throw new InvalidOperationException("Wait until current edits have saved, then preview the parts again.");
        string title = EffectiveDocumentTitle();
        if (title.Length > 180) title = title[..(char.IsHighSurrogate(title[179]) ? 179 : 180)];
        int created = 0;
        try
        {
            foreach (string part in parts)
            {
                _ = await _documentLibrary!.CreateAsync($"{title} - Part {created + 1:D3}", part).ConfigureAwait(true);
                created++;
            }
            return created;
        }
        finally
        {
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            StatusMessage = $"Created {created} local narration parts; original retained. Open each in Library and review/approve it separately. No audio generated.";
        }
    }
}
