using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task ExplicitJobCaptureHasNoRunTimerButStillConsumesOnlyOneExactResponse()
    {
        var clock = new Clock(); var store = new ProtectedStore(); var request = Request();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        Assert.Throws<InvalidOperationException>(() => observer.ArmForJob(request, false));
        observer.ArmForJob(request, true); clock.AdvanceMonotonic(TimeSpan.FromDays(10));
        Assert.True(observer.IsReadyFor(request)); Assert.False(observer.IsReadyFor(Request()));
        Guid ticket = observer.Begin(request);
        var result = await observer.ObserveAsync(ticket, request, 400,
            "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"Provider explanation.\"}}"u8.ToArray(),
            GoogleResponseReadState.Complete, "synthetic-token", false);
        Assert.True(result.ReadbackVerified); Assert.False(observer.IsReadyFor(request));
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(900_000)]
    public async Task LargeArticleCapturePreservesExplanationWhileMaskingInputAndCredential(int length)
    {
        var clock = new Clock(); var store = new ProtectedStore();
        string article = new string('x', length) + " PRIVATE-ARTICLE-CANARY-ALPHA-END";
        var request = Request(text: article);
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); Guid ticket = observer.Begin(request);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = "Provider confirmed sample failure. Echo: PRIVATE-ARTICLE-CANARY-ALPHA-END; credential=synthetic-token",
                details = new[] { new { reason = "Synthetic reason.", description = "Distinct nested explanation retained." } }
            }
        });
        var observed = await observer.ObserveAsync(ticket, request, 400, body, GoogleResponseReadState.Complete, "synthetic-token", false);
        Assert.True(observed.ReadbackVerified); Assert.False(observed.Truncated); Assert.False(observed.DetailsOmitted);
        var saved = Assert.Single(store.Records).Response!;
        Assert.Contains("Distinct nested explanation", saved.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-ARTICLE-CANARY", saved.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", saved.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 8), saved.RedactedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeUnicodeArticleMasksUnicodeJsonUrlAndBase64Echoes()
    {
        var clock = new Clock(); var store = new ProtectedStore();
        const string fragment = "独立中文隐私片段甲乙丙丁戊己";
        string article = new string('文', 50_000) + fragment;
        var request = Request(text: article); var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); Guid ticket = observer.Begin(request);
        string encoded = JsonSerializer.Serialize(fragment)[1..^1]; string url = Uri.EscapeDataString(fragment);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fragment));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = "Provider detail. " + fragment + " " + encoded + " " + url + " " + base64
            }
        });
        var observed = await observer.ObserveAsync(ticket, request, 400, body, GoogleResponseReadState.Complete, "synthetic-token", false);
        Assert.True(observed.ReadbackVerified);
        string saved = Assert.Single(store.Records).Response!.RedactedBody;
        Assert.DoesNotContain(fragment, saved, StringComparison.Ordinal); Assert.DoesNotContain(encoded, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(url, saved, StringComparison.Ordinal); Assert.DoesNotContain(base64, saved, StringComparison.Ordinal);
    }
}
