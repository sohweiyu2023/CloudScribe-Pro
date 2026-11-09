using CloudScribe.Application.Documents;
using NarrationUrlOmission = CloudScribe.Application.Documents.NarrationUrlOmissionV1;

namespace CloudScribe.Application.Tests;

public sealed class NarrationUrlOmissionTests
{
    [Theory]
    [InlineData("Read https://example.com now.", "Read  now.", 1)]
    [InlineData("Read HTTP://EXAMPLE.COM and HtTpS://example.org.", "Read  and .", 2)]
    [InlineData("See www.example.com or WWW.example.org/path.", "See  or .", 2)]
    [InlineData("See https://example.com/a%20b?q=one&next=%2Ftwo#part-3.", "See .", 1)]
    [InlineData("See https://example.com/?next=https://other.test/a#there now.", "See  now.", 1)]
    [InlineData("One https://example.com/a,b;c:d!e?x=1 two", "One  two", 1)]
    [InlineData("One https://example.com:8443/path two", "One  two", 1)]
    [InlineData("One http://localhost:8080/path two", "One  two", 1)]
    [InlineData("One http://127.0.0.1/a two", "One  two", 1)]
    [InlineData("One https://[2001:db8::1]:443/a two", "One  two", 1)]
    [InlineData("One https://user@example.com/a two", "One  two", 1)]
    [InlineData("https://a.test,https://b.test;www.c.test", ",;", 3)]
    [InlineData("https://a.test\r\nhttps://b.test", "\r\n", 2)]
    public void OmitsExplicitUrlsAndKeepsOtherText(string source, string expected, int count)
    {
        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal(expected, result.OutputText);
        Assert.Equal(count, result.OmittedUrlCount);
        Assert.Equal("url-omission-v1", result.TransformVersion);
    }

    [Theory]
    [InlineData("Read https://example.com. Next.", "Read . Next.")]
    [InlineData("Read https://example.com, then.", "Read , then.")]
    [InlineData("Read https://example.com?! Next.", "Read ?! Next.")]
    [InlineData("\"https://example.com\" and 'https://other.test'", "\"\" and ''")]
    [InlineData("“https://example.com” and ‘www.other.test’", "“” and ‘’")]
    [InlineData("(https://example.com/a(b)c).", "().")]
    [InlineData("https://example.com/a(b(c)d)e).", ").")]
    [InlineData("https://example.com/a(b)).", ").")]
    [InlineData("https://example.com/a(b end", " end")]
    [InlineData("[https://example.com/a]", "[]")]
    [InlineData("https://example.com/?q[]=one#two", "")]
    [InlineData("<https://example.com/a%28b%29>", "<>")]
    [InlineData("One\r\n\thttps://example.com\r\n\r\nTwo", "One\r\n\t\r\n\r\nTwo")]
    public void PreservesSentencePunctuationAndParagraphBoundaries(string source, string expected)
    {
        Assert.Equal(expected, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Theory]
    [InlineData("网址：https://例子.测试/路径？下一句。", "网址：？下一句。")]
    [InlineData("阅读 https://例子.测试/路径。然后继续。", "阅读 。然后继续。")]
    [InlineData("阅读https://example.com 路径", "阅读 路径")]
    [InlineData("详见https://example.com/page。然后继续", "详见。然后继续")]
    [InlineData("详见www.example.com/page，继续。", "详见，继续。")]
    [InlineData("参照https://example.com/page。次へ", "参照。次へ")]
    [InlineData("확인https://example.com/page。계속", "확인。계속")]
    [InlineData("https://例子.测试/路径，接着；还有！", "，接着；还有！")]
    [InlineData("www.例子.测试/路径：下一句", "：下一句")]
    [InlineData("https://example.com/path（注释）", "（注释）")]
    [InlineData("https://example.com/path、下一句", "、下一句")]
    [InlineData("https://example.com/path…继续", "…继续")]
    [InlineData("Café https://éxample.test/cafe\u0301/😀?q=你好 e\u0301 😀", "Café  e\u0301 😀")]
    [InlineData("Letter https://e\u0301xample.test/é remains", "Letter  remains")]
    [InlineData("😀https://example.com 𐐀", "😀 𐐀")]
    public void KeepsUnicodeProseAndOmitsUnicodeUrls(string source, string expected)
    {
        Assert.Equal(expected, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Theory]
    [InlineData("Read [the guide](https://example.com/a) now.", "Read the guide now.", 1)]
    [InlineData("Read [the guide](https://example.com/a(b)) now.", "Read the guide now.", 1)]
    [InlineData("Read [the guide](https://example.com/a.?!;) now.", "Read the guide now.", 1)]
    [InlineData("Read [the guide](<https://example.com/a>) now.", "Read the guide now.", 1)]
    [InlineData("Read [the guide]( https://example.com/a \"A title\" ) now.", "Read the guide now.", 1)]
    [InlineData("Read [the guide](www.example.com 'A title') now.", "Read the guide now.", 1)]
    [InlineData("Read [the [nested] guide](https://example.com) now.", "Read the [nested] guide now.", 1)]
    [InlineData("Read [the \\[nested\\] guide](https://example.com) now.", "Read the \\[nested\\] guide now.", 1)]
    [InlineData("[中文 😀 e\u0301](https://例子.测试/路径)", "中文 😀 e\u0301", 1)]
    [InlineData("[https://a.test](https://b.test)", "", 2)]
    [InlineData("[guide](https://example.com", "[guide](", 1)]
    [InlineData("[guide](https://example.com \"unfinished title", "[guide]( \"unfinished title", 1)]
    [InlineData("[guide](https://example.com extra text)", "[guide]( extra text)", 1)]
    public void PreservesMarkdownLabelsAndDoesNotConsumeMalformedProse(string source, string expected, int count)
    {
        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal(expected, result.OutputText);
        Assert.Equal(count, result.OmittedUrlCount);
    }

    [Theory]
    [InlineData(@"[guide](https://example.com/a\(b\))")]
    [InlineData(@"[guide](https://example.com/a\(b\) 'Title')")]
    [InlineData(@"[guide](<https://example.com/a\(b\)>)")]
    [InlineData(@"[guide](<https://example.com/a\>b>)")]
    [InlineData(@"[guide](https://example.com/a\))")]
    [InlineData(@"[guide](https://example.com/a\()")]
    [InlineData(@"[guide](https://example.com/a\?q\=1\&b\=2\#part)")]
    [InlineData(@"[guide](https://example.com\/a\(b\))")]
    [InlineData(@"[guide](https://example\.com/a)")]
    [InlineData(@"[guide](www.example\.com/a)")]
    [InlineData(@"[guide](https://example.com\:443/a)")]
    [InlineData(@"[guide](https://\[2001\:db8\:\:1\]/a)")]
    [InlineData(@"[guide](https://\[2001:db8::1]/a)")]
    [InlineData(@"[guide](https://example.com/a\[b])")]
    [InlineData(@"[guide](https://example.com/a,http://other.test/b\(c\))")]
    public void PreservesMarkdownLabelsWithEscapedDestinations(string source)
    {
        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal("guide", result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
    }

    [Fact]
    public void MarkdownDestinationAcceptsEveryEscapedAsciiPunctuationCharacter()
    {
        const string punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        foreach (char character in punctuation)
        {
            string source = $"[guide](https://example.com/a\\{character}b)";

            NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

            Assert.Equal("guide", result.OutputText);
            Assert.Equal(1, result.OmittedUrlCount);
        }
    }

    [Theory]
    [InlineData(@"[guide](https://example.com/a\(b\)", @"[guide](\(b\)")]
    [InlineData(@"[guide](https://example.com/a\)", @"[guide](\)")]
    [InlineData(@"[guide](https://example.com/a(a\))", @"[guide](\))")]
    [InlineData(@"[guide](https://example.com/a\", @"[guide](\")]
    [InlineData(@"[guide](https://example.com/a\q)", @"[guide](\q)")]
    [InlineData(@"[guide](<https://example.com/a\(b\))", @"[guide](<\(b\))")]
    [InlineData(@"[guide](https://example.com/a\) 'title'", @"[guide](\) 'title'")]
    [InlineData(@"Read https://example.com/a\(b\) now.", @"Read \(b\) now.")]
    public void MarkdownEscapesDoNotConsumeMalformedEndingsOrChangePlainUrlRules(string source, string expected)
    {
        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal(expected, result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Keep\r\n\t  spaces\n\n中文 😀 e\u0301.")]
    [InlineData("example.com, v1.2.3, archive.zip, notes.md and 1.0.4")]
    [InlineData("name@example.com and name@www.example.com")]
    [InlineData("name@例子www.example.com and 用户@中www.example.com")]
    [InlineData("name@𠀀www.example.com")]
    [InlineData("www.person@example.com and user+www.person@example.com")]
    [InlineData("[guide](www.person@example.com)")]
    [InlineData("prefixwww.example.com and prefixhttps://example.com")]
    [InlineData("e\u0301www.example.com and name_www.example.com")]
    [InlineData("\U00010400www.example.com")]
    [InlineData("ftp://example.com and mailto:name@example.com")]
    [InlineData("[the guide](relative/path.md)")]
    [InlineData("http:// https:// www.")]
    [InlineData("https:///path www..example.com")]
    [InlineData("https://-example.com https://example-.com")]
    [InlineData("https://example.com:65536/path https://example.com:notaport/")]
    [InlineData("https://[not-ipv6]/path")]
    [InlineData("https://example\ud800.test/path")]
    public void NoRecognizedUrlReturnsSameStringWithoutNormalization(string source)
    {
        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Same(source, result.OutputText);
        Assert.Equal(0, result.OmittedUrlCount);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, result.TransformVersion);
    }

    [Theory]
    [InlineData("https://example.com", false)]
    [InlineData("www.example.com\r\n(https://example.com).,;!?", false)]
    [InlineData("[https://a.test](https://b.test)", false)]
    [InlineData("[...](https://example.com)", false)]
    [InlineData(" ", false)]
    [InlineData("😀 \u0301 .?!", false)]
    [InlineData("中文 https://example.com 😀", true)]
    [InlineData("[中文](https://example.com)", true)]
    [InlineData("e\u0301", true)]
    [InlineData("\U00010400", true)]
    [InlineData("Ⅷ ½", true)]
    [InlineData("123", true)]
    public void ReportsLocalSpeakableContent(string source, bool expected)
    {
        Assert.Equal(expected, NarrationUrlOmission.Transform(source).HasSpeakableContent);
    }

    [Fact]
    public void NullAndOversizedInputFailWithoutTruncation()
    {
        Assert.Throws<ArgumentNullException>(() => NarrationUrlOmission.Transform(null!));
        string source = new('a', NarrationUrlOmission.MaximumSourceLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => NarrationUrlOmission.Transform(source));
    }

    [Fact]
    public void MaximumLengthInputWithoutUrlsIsUnchanged()
    {
        string source = new('a', NarrationUrlOmission.MaximumSourceLength);

        Assert.Same(source, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Fact]
    public void HugeUrlIsOmittedWithoutUriLengthLimitOrRegexBacktracking()
    {
        string source = string.Concat("Before https://example.com/", new string('a', 1_000_000), "?q=%20#part. After");

        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal("Before . After", result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
    }

    [Fact]
    public void HugeMalformedTokenIsPreserved()
    {
        string source = string.Concat("https://", string.Concat(Enumerable.Repeat("http://", 100_000)));

        // The first authority is a host with an invalid port; embedded prefixes are not retried.
        Assert.Same(source, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Fact]
    public void HugeUnicodeEmailTokenIsPreservedWithoutRepeatedBackwardScanning()
    {
        string source = string.Concat("name@", string.Concat(Enumerable.Repeat("中www.", 100_000)), "example.com");

        Assert.Same(source, NarrationUrlOmission.Transform(source).OutputText);
    }

    [Fact]
    public void HugeEscapedMarkdownDestinationPreservesOnlyItsLabel()
    {
        string source = string.Concat("[guide](https://example.com/", string.Concat(Enumerable.Repeat(@"a\(b\)", 100_000)), ")");

        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal("guide", result.OutputText);
        Assert.Equal(1, result.OmittedUrlCount);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(512)]
    [InlineData(8_192)]
    [InlineData(131_072)]
    public void RepeatedMalformedMarkdownFailsClosedAtEveryScale(int repetitions)
    {
        string source = string.Concat(Enumerable.Repeat(@"[x](https://a/\(", repetitions));

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() => NarrationUrlOmission.Transform(source));
        InvalidOperationException second = Assert.Throws<InvalidOperationException>(() => NarrationUrlOmission.Transform(source));

        // Deterministic budget rejection, not a machine-speed timeout or a partially omitted copy.
        Assert.Equal(first.Message, second.Message);
        Assert.Equal("URL omission could not prepare this text within its safe processing limit. "
            + "Simplify malformed Markdown links and try again.", first.Message);
        Assert.True(source.Length < NarrationUrlOmission.MaximumSourceLength);
    }

    [Fact]
    public void ComplexityFailureDoesNotReturnEarlierRemovalsOrConsumeUnrelatedProse()
    {
        const string before = "Keep this prose. https://example.com\r\n";
        const string after = "\r\nKeep this ending.";
        string source = string.Concat(before, string.Concat(Enumerable.Repeat(@"[x](https://a/\(", 512)), after);

        Assert.Throws<InvalidOperationException>(() => NarrationUrlOmission.Transform(source));
        Assert.StartsWith(before, source, StringComparison.Ordinal);
        Assert.EndsWith(after, source, StringComparison.Ordinal);
        Assert.Equal("Still usable", NarrationUrlOmission.Transform("[Still usable](https://example.com)").OutputText);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(512)]
    [InlineData(8_192)]
    public void ValidEscapedMarkdownStaysWithinBudgetAtEveryScale(int repetitions)
    {
        string source = string.Concat(Enumerable.Repeat(@"[x](https://a/\(b\)) ", repetitions));

        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal(repetitions, result.OmittedUrlCount);
        Assert.Equal(string.Concat(Enumerable.Repeat("x ", repetitions)), result.OutputText);
    }

    [Fact]
    public void ManyNestedMarkdownLabelsDoNotRequireRepeatedStringEdits()
    {
        const int links = 10_000;
        string source = string.Concat(new string('[', links), "Label", string.Concat(Enumerable.Repeat("](https://example.com)", links)));

        NarrationUrlOmissionResult result = NarrationUrlOmission.Transform(source);

        Assert.Equal("Label", result.OutputText);
        Assert.Equal(links, result.OmittedUrlCount);
    }

    [Fact]
    public void ResultIsDeterministicAndSourceIsNeverModified()
    {
        const string source = "A [guide](https://example.com/path?q=1#x)\r\nwww.example.com";

        NarrationUrlOmissionResult first = NarrationUrlOmission.Transform(source);
        NarrationUrlOmissionResult second = NarrationUrlOmission.Transform(source);

        Assert.Equal(first, second);
        Assert.Equal("A [guide](https://example.com/path?q=1#x)\r\nwww.example.com", source);
        Assert.Equal("A guide\r\n", first.OutputText);
    }
}
