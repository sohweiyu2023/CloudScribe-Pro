using System.Security.Cryptography;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ExactPricingControlMaterialInspectorTests
{
    [Fact]
    public void ExactIdentityAndStrictObjectAreAcceptedForIntakeOnly()
    {
        byte[] material = "{\"schemaVersion\":\"1.1.5\"}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, Sha256(material));

        Assert.True(result.IdentityMatched);
        Assert.True(result.StrictJsonObjectAccepted);
        Assert.Null(result.FormatError);
        Assert.Contains("separate gate", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityMismatchFailsClosedBeforeAdmission()
    {
        byte[] material = "{\"schemaVersion\":\"1.1.5\"}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, new string('0', 64));

        Assert.False(result.IdentityMatched);
        Assert.False(result.StrictJsonObjectAccepted);
        Assert.Contains("blocked", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchingIdentityStillRejectsHostileDuplicateMemberJson()
    {
        byte[] material = "{\"schemaVersion\":1,\"schemaVersion\":2}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, Sha256(material));

        Assert.True(result.IdentityMatched);
        Assert.False(result.StrictJsonObjectAccepted);
        Assert.Equal(PricingCatalogFormatError.DuplicateProperty, result.FormatError);
    }

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
