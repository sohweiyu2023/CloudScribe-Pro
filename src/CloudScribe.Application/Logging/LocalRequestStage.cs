namespace CloudScribe.Application.Logging;

public enum LocalRequestStage
{
    Unknown, ArticleOpen, CoverageRead, Review, Reservation, OutputRepair, Export, CaptureOpen, CaptureArm, CaptureRead,
    RecoveryOpen, RecoveryReview, RecoveryReservation,
    DocumentCreate, DocumentOpen, LibraryLoad, LibrarySearch, DocumentSave, DocumentAutosave, ObsoleteAutosave,
    DocumentRestore, DocumentImport, DocumentPostSavePresentation,
    ConnectionRead, PreferenceWrite, PricingActivation, GenerationDispatch,
}
