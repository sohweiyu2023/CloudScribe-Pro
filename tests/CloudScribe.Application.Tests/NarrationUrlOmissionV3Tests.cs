using CloudScribe.Application.Documents;

namespace CloudScribe.Application.Tests;

/// <summary>v3 keeps prose that v2 deleted as "bare domains", while still skipping real addresses. v2 output stays
/// byte-for-byte unchanged for saved copies that recorded it.</summary>
public sealed class NarrationUrlOmissionV3Tests
{
    [Theory]
    [InlineData("Rest in pure awareness.In this state, nothing is lacking.", "Rest in pure  this state, nothing is lacking.")]
    [InlineData("This is for us.Us and them dissolve.", "This is for  and them dissolve.")]
    [InlineData("Read the sutra.Info about it follows.", "Read the  about it follows.")]
    [InlineData("It is net.Net result is clear.", "It is  result is clear.")]
    [InlineData("The Dharma.App store is not relevant.", "The  store is not relevant.")]
    [InlineData("See section 3.in the appendix.", "See section  the appendix.")]
    [InlineData("In 2024.de facto rules changed.", "In  facto rules changed.")]
    [InlineData("the end.in the beginning", "the  the beginning")]
    public void V3KeepsSentenceTextThatV2Deleted(string source, string v2Output)
    {
        var v2 = NarrationUrlOmission.Transform(source, NarrationUrlOmission.BareDomainV2Version);
        Assert.Equal(v2Output, v2.OutputText); // the defect, replayed exactly for copies that recorded v2
        Assert.Equal("url-omission-v2", v2.TransformVersion);

        var v3 = NarrationUrlOmission.Transform(source);
        Assert.Equal(source, v3.OutputText);
        Assert.Equal(0, v3.OmittedUrlCount);
        Assert.Equal("url-omission-v3", v3.TransformVersion);
    }

    [Theory]
    [InlineData("Read 84000.co now.", "Read  now.", 1)]
    [InlineData("(84000.CO), reading.84000.co;", "(), ;", 2)]
    [InlineData("84000.co,example.org;EXAMPLE.COM", ",;", 3)]
    [InlineData("Visit awakeningtoreality.com for more.", "Visit  for more.", 1)]
    [InlineData("See read.84000.co:443/translation?q=one%20two#part.", "See .", 1)]
    [InlineData("Read example.co.uk and 84000.co/a(b)c.", "Read  and .", 2)]
    [InlineData("example.in/path and shop.example.in and www.example.us", " and  and ", 3)]
    [InlineData("https://example.In/x stays an explicit URL", " stays an explicit URL", 1)]
    [InlineData("[the guide](84000.co/a(b)c)", "the guide", 1)]
    [InlineData("〈84000.co〉", "〈〉", 1)]
    public void V3StillSkipsRealAddresses(string source, string expected, int count)
    {
        var result = NarrationUrlOmission.Transform(source, NarrationUrlOmission.CurrentVersion);
        Assert.Equal(expected, result.OutputText);
        Assert.Equal(count, result.OmittedUrlCount);
    }

    [Fact]
    public void LowercaseRunOnThatLooksLikeADomainRemainsAmbiguous()
    {
        const string source = "There is nothing.ai follows.";
        var result = NarrationUrlOmission.Transform(source);
        Assert.Equal("There is  follows.", result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
        Assert.Equal(source, NarrationUrlOmission.Transform(source, NarrationUrlOmission.LegacyVersion).OutputText);
    }

    [Fact]
    public void AllThreePoliciesStaySupportedAndDistinct()
    {
        Assert.Equal("url-omission-v3", NarrationUrlOmission.CurrentVersion);
        Assert.True(NarrationUrlOmission.IsSupportedVersion("url-omission-v1"));
        Assert.True(NarrationUrlOmission.IsSupportedVersion("url-omission-v2"));
        Assert.True(NarrationUrlOmission.IsSupportedVersion("url-omission-v3"));
        Assert.False(NarrationUrlOmission.IsSupportedVersion("url-omission-v4"));
        Assert.Throws<InvalidOperationException>(() => NarrationUrlOmission.Transform("x", "url-omission-v4"));
        const string text = "awareness.In 84000.co";
        Assert.Equal("awareness.In 84000.co", NarrationUrlOmission.Transform(text, NarrationUrlOmission.LegacyVersion).OutputText);
        Assert.Equal(" ", NarrationUrlOmission.Transform(text, NarrationUrlOmission.BareDomainV2Version).OutputText);
        Assert.Equal("awareness.In ", NarrationUrlOmission.Transform(text).OutputText);
    }
}
