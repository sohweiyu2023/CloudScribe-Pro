using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;

namespace CloudScribe.Application.Tests;

public sealed class NarrationUrlOmissionVersionTests
{
    [Theory]
    [InlineData("Read 84000.co now.", "Read  now.", 1)]
    [InlineData("prefixwww.example.com and prefixhttps://example.com", " and prefixhttps://example.com", 1)]
    [InlineData("(84000.CO), reading.84000.co;", "(), ;", 2)]
    [InlineData("See read.84000.co:443/translation?q=one%20two#part.", "See .", 1)]
    [InlineData("84000.co,example.org;EXAMPLE.COM", ",;", 3)]
    [InlineData("https://example.test,84000.co;www.example.test", ",;", 3)]
    [InlineData("84000.co,https://example.test;84000.co", ",;", 3)]
    [InlineData("Read example.co.uk and 84000.co/a(b)c.", "Read  and .", 2)]
    [InlineData("84000.co/?q[]=one#two", "", 1)]
    [InlineData("[the guide](84000.co/a(b)c)", "the guide", 1)]
    [InlineData("[the guide](<84000.co/a> \"Title\")", "the guide", 1)]
    [InlineData("84000.co\r\n\tRead\nexample.org!", "\r\n\tRead\n!", 2)]
    [InlineData("\u300884000.co\u3009", "\u3008\u3009", 1)]
    public void RecognizesConservativeBareDomains(string source, string expected, int count)
    {
        var result = NarrationUrlOmission.Transform(source);
        Assert.Equal(expected, result.OutputText);
        Assert.Equal(count, result.OmittedUrlCount);
        Assert.Equal("url-omission-v2", result.TransformVersion);
        Assert.Equal(result, NarrationUrlOmission.Transform(source, NarrationUrlOmission.CurrentVersion));
    }

    [Theory]
    [InlineData("archive.zip, notes.md, this.is, v1.2.3, 1.0.4 and 12.34")]
    [InlineData("name@84000.co user+name@84000.co name@sub.84000.co")]
    [InlineData("ftp://84000.co mailto:84000.co file:84000.co custom://84000.co")]
    [InlineData("/84000.co ../84000.co C:\\84000.co folder/84000.co")]
    [InlineData("_84000.co +84000.co -84000.co %84000.co ~84000.co =84000.co")]
    [InlineData("84000.co.txt 84000.co.invalid .84000.co 84000..co 84000.co..")]
    [InlineData("-84000.co 84000-.co 84000._co 84000.co_ 84000.co@name")]
    [InlineData("84000.co:65536 84000.co:abc 84000.co:443:443 84000.co:%34")]
    [InlineData("84000.co/a%2 84000.co/a%ZZ 84000.co/a(b 84000.co/a[b")]
    [InlineData("co.uk org.uk ac.uk gov.uk com.au net.au org.au co.nz")]
    [InlineData("/(84000.co) path/(84000.co) name@(84000.co) prefix(84000.co)")]
    [InlineData(@"84000.co\file 84000.co:443\file \[84000.co\]")]
    [InlineData("xn--bcher-kva.com books.xn--p1ai books.xn--bcher-kva.com")]
    [InlineData("\u4e2d84000.co 84000.co\u6587 84000.co/\u4e2d\u6587")]
    [InlineData("\u00e984000.co 84000.co\u00e9 e\u030184000.co 84000.co\u0301")]
    [InlineData("\U0001f60084000.co 84000.co\U00010400 84000.co\u200b")]
    [InlineData("84000.co\ud800 \udc0084000.co")]
    public void PreservesAmbiguousMalformedAndNonUrlTokens(string source)
    {
        var result = NarrationUrlOmission.Transform(source);
        Assert.Same(source, result.OutputText);
        Assert.Equal(0, result.OmittedUrlCount);
    }

    [Fact]
    public void LegacyOutputAndUnsupportedVersionAreNotSilentlyUpgraded()
    {
        const string source = "Read 84000.co and https://example.com.";
        var legacy = NarrationUrlOmission.Transform(source, NarrationUrlOmission.LegacyVersion);
        Assert.Equal("Read 84000.co and .", legacy.OutputText);
        Assert.Equal("url-omission-v1", legacy.TransformVersion);
        Assert.Equal("Read  and .", NarrationUrlOmission.Transform(source).OutputText);
        foreach (string? version in new string?[] { null, "", "url-omission-v3", "URL-OMISSION-V1" })
        {
            Assert.False(NarrationUrlOmission.IsSupportedVersion(version));
            Assert.Throws<InvalidOperationException>(() => NarrationUrlOmission.Transform(source, version));
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(100_000)]
    public void InvalidOversizedHostsAndTokensDoNotExposeAnEmbeddedDomain(int length)
    {
        string source = new string('a', length) + ".84000.co";
        Assert.Same(source, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Fact]
    public void LongValidBarePathsUseTheSharedSourceBudgetWithoutAnArbitraryUrlLimit()
    {
        string source = "Before 84000.co/" + new string('a', 1_000_000) + "?q=one%20two#part. After";
        var result = NarrationUrlOmission.Transform(source);
        Assert.Equal("Before . After", result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
    }

    [Fact]
    public void RepeatedInvalidEmailSchemeAndPathTokensStayBoundedAndUnchanged()
    {
        string source = string.Concat(Enumerable.Repeat("user@84000.co ftp://84000.co /84000.co ", 100_000));
        Assert.Same(source, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Fact]
    public void OldDraftsResolveConservativelyAndNewDraftsPinCurrentPolicy()
    {
        var legacy = new BatchDraft(BatchDraft.CurrentVersion, null, null, false, BatchEngines.LongAudioFirst,
            BatchFailurePolicy.SkipAndContinue, [], DateTimeOffset.UnixEpoch, SkipUrls: true);
        Assert.Equal(NarrationUrlOmission.LegacyVersion, legacy.ResolveUrlTransformVersion());
        Assert.Equal(NarrationUrlOmission.CurrentVersion, (legacy with { SkipUrls = false }).ResolveUrlTransformVersion());
        Assert.Equal(NarrationUrlOmission.CurrentVersion, BatchDraft.Empty(DateTimeOffset.UnixEpoch).ResolveUrlTransformVersion());
        Assert.Throws<InvalidOperationException>(() => (legacy with { UrlTransformVersion = "unknown" }).ResolveUrlTransformVersion());
    }
}
