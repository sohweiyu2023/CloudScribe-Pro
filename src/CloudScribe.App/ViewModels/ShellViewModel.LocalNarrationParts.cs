using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private int _localPartCreationInFlight;
    public async Task<int> CreateLocalNarrationPartsAsync(IReadOnlyList<string> parts, string originalText)
    {
        EnsureDocumentWorkflowConfigured();
        if (!HasOpenDocument || !string.Equals(DocumentText, originalText, StringComparison.Ordinal))
            throw new InvalidOperationException("The source changed. Preview narration parts again.");
        if (parts.Count is < 1 or > 100 || !string.Equals(string.Concat(parts), originalText, StringComparison.Ordinal))
            throw new InvalidOperationException("Narration parts must preserve the exact complete source text.");
        Guid sourceDocumentId = _currentDocumentId!.Value;
        string sourceTitle = EffectiveDocumentTitle();
        parts = parts.ToArray();
        if (Interlocked.CompareExchange(ref _localPartCreationInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A local preparation is already in progress.");
        int created = 0;
        bool grouped = false;
        try
        {
            if (IsDocumentDirty && !await SaveCurrentDocumentCoreAsync("Before creating local narration parts").ConfigureAwait(true))
                throw new InvalidOperationException("Wait until current edits have saved, then preview parts again.");
            if (_currentDocumentId != sourceDocumentId || !string.Equals(DocumentText, originalText, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected original changed during save; preview its parts again.");
            var parent = await _documentLibrary!.OpenAsync(sourceDocumentId).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The original is missing.");
            if (!string.Equals(parent.Text, originalText, StringComparison.Ordinal) || parent.CurrentRevisionId is not Guid revision)
                throw new InvalidOperationException("The original changed. Preview its parts again.");
            var store = _narrationPartGroups ?? throw new InvalidOperationException("Durable narration grouping is unavailable; no parts were created.");
            static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));
            var preparation = await store.BeginAsync(parent.Id, revision, Hash(originalText), parts.Select(Hash).ToArray()).ConfigureAwait(true);
            if (preparation.AlreadyComplete)
            {
                grouped = true;
                return 0;
            }
            string title = sourceTitle;
            if (title.Length > 180) title = title[..(char.IsHighSurrogate(title[179]) ? 179 : 180)];
            List<NarrationPartMember> members = [];
            foreach (string part in parts)
            {
                var child = await _documentLibrary.CreateAsync($"{title} - Part {created + 1:D3}", part).ConfigureAwait(true);
                members.Add(new(child.Id, child.CurrentRevisionId ?? throw new InvalidOperationException("Part has no saved revision."), Hash(child.Text)));
                created++;
            }
            await store.RegisterAsync(preparation.GroupId, members).ConfigureAwait(true);
            grouped = true;
            return created;
        }
        finally
        {
            Volatile.Write(ref _localPartCreationInFlight, 0);
            await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            StatusMessage = grouped
                ? "Local narration parts are grouped beneath the original. Review each part separately; no audio generated."
                : $"Created {created} local parts; original retained. Incomplete preparation remains visible and is not automatically repeated.";
        }
    }
}
