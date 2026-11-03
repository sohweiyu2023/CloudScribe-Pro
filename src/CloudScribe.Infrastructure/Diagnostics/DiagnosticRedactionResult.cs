namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Redaction is not anonymity; private provider text must remain private.</summary>
internal sealed record DiagnosticRedactionResult(string Text, bool WasTruncated)
{
    public override string ToString() => "Redacted diagnostic text (contents omitted)";
}
