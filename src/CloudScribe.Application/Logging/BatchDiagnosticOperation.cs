namespace CloudScribe.Application.Logging;

public enum BatchDiagnosticOperation
{
    Unknown, Run, Narrate, VerifyOutput, ReadPrimary, ReadBackup, Save, List, AcquireRunLock,
    Window, LoadSaved, AddFiles, AddFolder, ChooseOutput, Reorder, Remove, Retry, CheckAgain,
    Skip, SaveAndImport, Rehearse, ExportApproval, Approve, WriteManifest, Pause, Stop, Close,
    NewBatch, Review, Refresh,
    SaveDraft, LoadDraft, DiscardDraft, Undo, Drop, DragReorder, ChooseVoice, RefreshVoices, OpenOutput, OpenFile,
    CancelWork, ImportSaved, ChangeSettings, PrepareNarration,
}
