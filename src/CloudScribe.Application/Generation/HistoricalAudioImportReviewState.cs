namespace CloudScribe.Application.Generation;

/// <summary>UI review invalidation only; this object cannot admit evidence or authorize synthesis.</summary>
public sealed class HistoricalAudioImportReviewState
{
    private ArticleHistoricalImportPreview? _preview;
    private bool _confirmed;
    public int SelectionVersion { get; private set; }
    public string? EvidenceId { get; private set; }
    public bool CanImport => _preview is not null && _confirmed;

    public void SelectEvidence(string? evidenceId)
    {
        EvidenceId = evidenceId; SelectionVersion++; _preview = null; _confirmed = false;
    }

    public bool PresentPreview(int capturedVersion, ArticleHistoricalImportPreview preview)
    {
        if (capturedVersion != SelectionVersion || string.IsNullOrWhiteSpace(EvidenceId)) return false;
        _preview = preview; _confirmed = false; return true;
    }

    public void ConfirmExactReview(bool confirmed) => _confirmed = confirmed && _preview is not null;

    public ArticleHistoricalImportPreview TakeConfirmedPreview()
    {
        if (!CanImport) throw new InvalidOperationException("Review and confirm the exact admitted historical output first.");
        var result = _preview!; _preview = null; _confirmed = false; return result;
    }
}
