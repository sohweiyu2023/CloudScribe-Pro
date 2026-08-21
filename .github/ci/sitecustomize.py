from __future__ import annotations

from pathlib import Path

_ORIGINAL_WRITE_TEXT = Path.write_text

_RECORD_BLOCK = '''public sealed record PricingControlMaterialInspection(
    bool IdentityMatched,
    string ActualSha256,
    PricingCatalogFormatError? FormatError,
    string StatusReason)
{
    public bool StrictJsonObjectAccepted => IdentityMatched && FormatError is null;
}

public sealed class ExactPricingControlMaterialInspector
{
'''

_NESTED_RECORD_BLOCK = '''public sealed class ExactPricingControlMaterialInspector
{
    public sealed record PricingControlMaterialInspection(
        bool IdentityMatched,
        string ActualSha256,
        PricingCatalogFormatError? FormatError,
        string StatusReason)
    {
        public bool StrictJsonObjectAccepted => IdentityMatched && FormatError is null;
    }

'''


def _write_text_lf(self: Path, data: str, encoding=None, errors=None, newline=None):
    normalized = str(self).replace('\\', '/')

    if normalized.endswith('/src/CloudScribe.Infrastructure/Pricing/ExactPricingControlMaterialInspector.cs'):
        if data.count(_RECORD_BLOCK) != 1:
            raise RuntimeError('Unexpected ExactPricingControlMaterialInspector source shape.')
        data = data.replace(_RECORD_BLOCK, _NESTED_RECORD_BLOCK, 1)

    if normalized.endswith('/tests/CloudScribe.Infrastructure.Tests/ExactPricingControlMaterialInspectorTests.cs'):
        data = data.replace(
            'PricingControlMaterialInspection result = inspector.Inspect(',
            'ExactPricingControlMaterialInspector.PricingControlMaterialInspection result = inspector.Inspect(',
        )

    # Repository policy is LF. Force LF even on the Windows certification host so
    # generated substantive candidate files pass the pre-freeze formatting gate.
    return _ORIGINAL_WRITE_TEXT(self, data, encoding=encoding, errors=errors, newline='\n')


Path.write_text = _write_text_lf
