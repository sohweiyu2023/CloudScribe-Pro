namespace CloudScribe.Domain.Generation;

/// <summary>Informational local pattern match only; never canonical cause or retry authority.</summary>
public enum GoogleValidationMessageHint
{
    NotProvided, Unrecognized, GenericValidation, SentenceLengthReported, OverHintBound, NotExamined,
}
