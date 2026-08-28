using System.Text;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Infrastructure.Tests;

public sealed class StrictJsonObjectReaderTests
{
    private readonly StrictJsonObjectReader _reader = new(maximumDocumentBytes: 4096, maximumDepth: 8);

    [Fact]
    public void AcceptsOneStrictUtf8Object()
    {
        using var document = _reader.Parse(Encoding.UTF8.GetBytes("{\"schema_version\":\"1.1.5\",\"items\":[{\"id\":1}]}"));
        Assert.Equal("1.1.5", document.RootElement.GetProperty("schema_version").GetString());
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", PricingCatalogFormatError.DuplicateProperty)]
    [InlineData("{\"outer\":{\"a\":1,\"a\":2}}", PricingCatalogFormatError.DuplicateProperty)]
    [InlineData("[]", PricingCatalogFormatError.TopLevelNotObject)]
    [InlineData("{\"a\":NaN}", PricingCatalogFormatError.InvalidJson)]
    [InlineData("{\"a\":Infinity}", PricingCatalogFormatError.InvalidJson)]
    [InlineData("{\"a\":1,}", PricingCatalogFormatError.InvalidJson)]
    [InlineData("{/*comment*/\"a\":1}", PricingCatalogFormatError.InvalidJson)]
    public void RejectsHostileOrNonStrictJson(string json, PricingCatalogFormatError expected)
    {
        PricingCatalogFormatException error = Assert.Throws<PricingCatalogFormatException>(() => _reader.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void RejectsInvalidUtf8AndOversizePayloads()
    {
        byte[] invalidUtf8 = [0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d];
        Assert.Equal(PricingCatalogFormatError.InvalidJson, Assert.Throws<PricingCatalogFormatException>(() => _reader.Parse(invalidUtf8)).Error);
        byte[] oversized = new byte[4097];
        Assert.Equal(PricingCatalogFormatError.TooLarge, Assert.Throws<PricingCatalogFormatException>(() => _reader.Parse(oversized)).Error);
    }
}
