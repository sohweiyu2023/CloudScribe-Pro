namespace CloudScribe.Domain.Documents;

public enum DocumentRevisionKind
{
    Autosave = 0,
    Checkpoint = 1,
    Import = 2,
    Recovery = 3,
}
