using System.Reflection;
using CloudScribe.App.Composition;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed class BatchDraftVersionTests
{
    [Fact]
    public void RetainedLegacyPolicyIsExplainedRatherThanSilentlyUsingTheSameCheckbox()
    {
        var explain = typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("BatchUrlPolicyText", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Contains("Bare addresses such as 84000.co remain", Assert.IsType<string>(explain.Invoke(null, [NarrationUrlOmission.LegacyVersion])), StringComparison.Ordinal);
        Assert.Contains("Bare-domain skipping is enabled", Assert.IsType<string>(explain.Invoke(null, [NarrationUrlOmission.CurrentVersion])), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("url-omission-v1")]
    [InlineData("url-omission-v2")]
    public void CopyVersionSurvivesSessionRestoreSaveAndDiscardUndo(string version)
    {
        Type binder = typeof(GoogleTtsUsabilityHotfixBinder);
        Type sessionType = binder.GetNestedType("BatchSession", BindingFlags.NonPublic)!;
        object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        var item = new BatchItem(Guid.NewGuid(), 1, "copy.txt", "source", 1, 1, "copy.mp3", BatchItemState.Ready,
            DocumentId: Guid.NewGuid(), RevisionId: Guid.NewGuid(), NarrationTextSha256: "narration",
            NarrationSource: new(Guid.NewGuid(), Guid.NewGuid(), "source", version, 1));
        // An old copy-to-batch draft did not carry UrlTransformVersion. Infer it only from actual provenance.
        var draft = BatchDraft.Empty(DateTimeOffset.UnixEpoch) with { SkipUrls = true, UrlTransformVersion = null, Items = [item] };
        var restore = binder.GetMethod("ApplyDraftSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        var capture = sessionType.GetMethod("ToDraft")!;
        restore.Invoke(null, [session, draft]);
        var snapshot = Assert.IsType<BatchDraft>(capture.Invoke(session, null));
        Assert.Equal(version, snapshot.UrlTransformVersion);
        Assert.Equal(item, Assert.Single(snapshot.Items));
        restore.Invoke(null, [session, BatchDraft.Empty(DateTimeOffset.UnixEpoch)]);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, Assert.IsType<BatchDraft>(capture.Invoke(session, null)).UrlTransformVersion);
        restore.Invoke(null, [session, snapshot]);
        Assert.Equal(version, Assert.IsType<BatchDraft>(capture.Invoke(session, null)).UrlTransformVersion);
        var mixed = snapshot with { Items = [item, item with { ItemId = Guid.NewGuid(), NarrationSource = item.NarrationSource! with { TransformVersion = "unknown" } }] };
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => restore.Invoke(null, [session, mixed])).InnerException);
        Assert.Equal(version, Assert.IsType<BatchDraft>(capture.Invoke(session, null)).UrlTransformVersion);
    }
}
