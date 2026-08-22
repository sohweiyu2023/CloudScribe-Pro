using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationReleaseReceiptTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Create_IsDeterministicAcrossInputOrder_AndVerifies()
    {
        var collection = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var a = new GenerationReleaseSegmentReceipt(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "cache-a", ShaA, "proof-a", true);
        var b = new GenerationReleaseSegmentReceipt(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "cache-b", ShaB, "proof-b", true);

        var first = GenerationReleaseReceipt.Create(collection, 7, "pricing-v7", "approval-v7", @"C:\release\book.wav", ShaA, new[] { b, a });
        var second = GenerationReleaseReceipt.Create(collection, 7, "pricing-v7", "approval-v7", @"C:\release\book.wav", ShaA, new[] { a, b });

        Assert.Equal(first.ReceiptSha256, second.ReceiptSha256);
        Assert.True(first.Verify());
    }

    [Fact]
    public void Create_RejectsQuarantinedSegment()
    {
        var item = new GenerationReleaseSegmentReceipt(Guid.NewGuid(), "cache", ShaA, "proof", false);
        Assert.Throws<InvalidOperationException>(() => GenerationReleaseReceipt.Create(Guid.NewGuid(), 1, "pricing", "approval", @"C:\x.wav", ShaB, new[] { item }));
    }

    [Fact]
    public void Verify_FailsAfterReceiptIdentityTamper()
    {
        var item = new GenerationReleaseSegmentReceipt(Guid.NewGuid(), "cache", ShaA, "proof", true);
        var receipt = GenerationReleaseReceipt.Create(Guid.NewGuid(), 1, "pricing", "approval", @"C:\x.wav", ShaB, new[] { item });
        Assert.False((receipt with { ApprovalId = "different-approval" }).Verify());
    }
}
