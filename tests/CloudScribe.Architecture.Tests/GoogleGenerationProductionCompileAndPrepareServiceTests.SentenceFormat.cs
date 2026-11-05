using CloudScribe.App.Composition;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleGenerationProductionCompileAndPrepareServiceTests
{
    [Fact]
    public async Task SentenceFormatProductionCompilerPreservesExactWireAndRejectsPostCompilationLimit()
    {
        const string voice = "cmn-CN-Chirp3-HD-Alnilam";
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var service = CreateService(owner, CreatePersistedAccount("credential-current"), "capability-1");
        var options = new GoogleSpeechCompilationOptions("cmn-CN", voice, "MP3", 5000) { SentenceMarkers = new(1, [100, 200]) };
        var original = CreateEvidence("credential-current", "capability-1");
        var evidence = original with
        {
            Plan = new("cmn-CN", [new SpeechText(new string('a', 99) + "." + new string('b', 99) + "?")], "synthetic-exact-source"),
            CompilationOptions = options,
            Capabilities = original.Capabilities with { VoiceNames = new HashSet<string>(StringComparer.Ordinal) { voice } },
            AdmittedTrust = original.AdmittedTrust with { VoiceStableId = voice, LanguageTag = "cmn-CN" },
        };
        var pending = await service.CompileForReviewAsync(evidence, TestContext.Current.CancellationToken);
        var expected = GoogleSpeechPlanCompiler.Compile(evidence.Plan, options);
        Assert.Equal(expected.Payload.ToArray(), pending.Snapshot.ProviderRequest.CompiledPayload.ToArray());
        Assert.Equal(expected.PayloadSha256, pending.Envelope.CompiledPayloadSha256);
        Assert.Equal(voice, pending.Snapshot.UiSelection.VoiceId);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompileForReviewAsync(evidence with
        { Capabilities = evidence.Capabilities with { MaximumCompiledPayloadBytes = expected.Payload.Length - 1 } }, TestContext.Current.CancellationToken));
    }
}
