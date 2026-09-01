using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabEvidenceAuthorizedAuditionExecutorTests
{
    [Fact]
    public async Task MatchingCurrentEvidenceResolvesAndSubmitsExactRequestAndFreshEvidence()
    {
        var approved = CurrentEvidence();
        VoiceLabAuditionRequest? resolved = null;
        VoiceLabAuditionRequest? submitted = null;
        VoiceLabAuditionAuthorizationEvidence? submittedEvidence = null;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (request, _) =>
            {
                resolved = request;
                return Task.FromResult(approved);
            },
            (request, evidence, _) =>
            {
                submitted = request;
                submittedEvidence = evidence;
                return Task.FromResult(Accepted());
            });
        var request = CurrentRequest();

        var response = await executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Same(request, resolved);
        Assert.Same(request, submitted);
        Assert.Same(approved, submittedEvidence);
    }

    [Fact]
    public async Task ChangedCredentialReferenceFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var current = approved with { CredentialReferenceId = "credential-b" };
        var submitCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task ChangedPricingEvidenceFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var current = approved with { PricingEvidenceId = "pricing-b" };
        var submitCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task ChangedSpendAuthorizationFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var current = approved with { SpendAuthorizationId = "spend-b" };
        var submitCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task RevokedSpendApprovalFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var current = approved with { SpendApproved = false };
        var submitCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task StalePricingFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var current = approved with { PricingCurrent = false };
        var submitCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, submitCalls);
    }

    private static VoiceLabAuditionRequest CurrentRequest() => new(
        CurrentSelection(),
        CachePolicyEligible: false,
        ForceFresh: true,
        ExplicitSpendApproved: true,
        PricingCurrent: true,
        OutputFormat: "wav");

    private static VoiceLabAuditionAuthorizationEvidence CurrentEvidence() => new(
        CurrentSelection(),
        CredentialReferenceId: "credential-a",
        PricingEvidenceId: "pricing-a",
        SpendAuthorizationId: "spend-a",
        PricingCurrent: true,
        SpendApproved: true);

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

    private static GenerationProviderResponse Accepted() => new(
        SubmissionDisposition.Accepted,
        "audition-request",
        ReadOnlyMemory<byte>.Empty,
        "audio/wav",
        null,
        "audition-accepted");
}
