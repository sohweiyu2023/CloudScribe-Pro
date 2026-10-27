namespace CloudScribe.Domain.Generation;

public enum GoogleErrorBodyParseState
{
    Empty, OverDiagnosticBound, InvalidJson, Ambiguous, ValidError, MissingError, InvalidErrorShape,
}
