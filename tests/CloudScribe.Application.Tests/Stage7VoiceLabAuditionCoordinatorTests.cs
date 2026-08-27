using System.Buffers.Binary;
using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabAuditionCoordinatorTests
{
    [Fact]
    public async Task Eligible_cache_hit_avoids_provider_spend()
    {
        var wav = MinimalPcmWave();
        var submitCalls = 0;
        var coordinator = new VoiceLabAuditionCoordinator(
            _ => Task.FromResult<ReadOnlyMemory<byte>?>(wav),
            _ =>
            {
                submitCalls++;
                return Task.FromResult(Accepted(wav));
            });

        var request = new VoiceLabAuditionRequest(
            CurrentSelection(),
            CachePolicyEligible: true,
            ForceFresh: false,
            ExplicitSpendApproved: false,
            PricingCurrent: true,
            OutputFormat: "wav");
        var outcome = await coordinator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(outcome.CacheHit);
        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task Force_fresh_with_approval_submits_once()
    {
        var wav = MinimalPcmWave();
        var submitCalls = 0;
        var coordinator = new VoiceLabAuditionCoordinator(
            _ => Task.FromResult<ReadOnlyMemory<byte>?>(wav),
            _ =>
            {
                submitCalls++;
                return Task.FromResult(Accepted(wav));
            });

        var request = new VoiceLabAuditionRequest(
            CurrentSelection(),
            CachePolicyEligible: true,
            ForceFresh: true,
            ExplicitSpendApproved: true,
            PricingCurrent: true,
            OutputFormat: "wav");
        var outcome = await coordinator.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.False(outcome.CacheHit);
        Assert.Equal(1, submitCalls);
    }

    private static VoiceLabCatalogSelection CurrentSelection() => new(
        VoiceStableId: "voice-a",
        ProviderStableId: "google-cloud-text-to-speech",
        AccountStableId: "account-a",
        ProjectStableId: "project-a",
        CapabilityEvidenceId: "capability-a",
        VoiceFingerprint: "fingerprint-a",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);

    private static GenerationProviderResponse Accepted(byte[] wav) => new(
        SubmissionDisposition.Accepted,
        "audition-request",
        wav,
        "audio/wav",
        null,
        "audition-accepted");

    private static byte[] MinimalPcmWave()
    {
        var bytes = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 36);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 0);
        return bytes;
    }
}
