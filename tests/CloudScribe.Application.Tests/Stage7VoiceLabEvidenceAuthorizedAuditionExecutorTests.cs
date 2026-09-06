using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabEvidenceAuthorizedAuditionExecutorTests
{
    [Fact]
    public async Task MatchingCurrentEvidenceSubmitsBoundProviderRequestAndDisposesAdapter()
    {
        var approved = CurrentEvidence();
        var evidenceReads = 0;
        var selectionReads = 0;
        var adapter = new RecordingAuditionAdapter(approved.Selection.ProviderStableId);
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (request, _) =>
            {
                Assert.Same(CurrentRequestSelectionHolder.Selection, request.Selection);
                evidenceReads++;
                return Task.FromResult(approved);
            },
            (selection, _) =>
            {
                selectionReads++;
                Assert.Equal(approved.Selection, selection);
                return Task.FromResult(approved.Selection);
            },
            (providerStableId, accountStableId, _) =>
            {
                Assert.Equal(approved.Selection.ProviderStableId, providerStableId);
                Assert.Equal(approved.Selection.AccountStableId, accountStableId);
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(adapter);
            });
        var request = CurrentRequestSelectionHolder.Request;

        var response = await executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal(2, evidenceReads);
        Assert.Equal(1, selectionReads);
        Assert.Equal(1, adapter.SubmitCalls);
        Assert.Equal(1, adapter.DisposeCalls);
        var providerRequest = Assert.IsType<VoiceLabProviderAuditionRequest>(adapter.LastRequest);
        Assert.Equal(approved.Selection.ProviderStableId, providerRequest.ProviderStableId);
        Assert.Equal(approved.Selection.AccountStableId, providerRequest.AccountStableId);
        Assert.Equal(approved.Selection.ProjectStableId, providerRequest.ProjectStableId);
        Assert.Equal(approved.Selection.VoiceStableId, providerRequest.VoiceStableId);
        Assert.Equal(approved.Selection.VoiceFingerprint, providerRequest.VoiceFingerprint);
        Assert.Equal(approved.Selection.CapabilityEvidenceId, providerRequest.CapabilityEvidenceId);
        Assert.Equal(approved.CredentialReferenceId, providerRequest.CredentialReferenceId);
        Assert.Equal(approved.PricingEvidenceId, providerRequest.PricingEvidenceId);
        Assert.Equal(approved.SpendAuthorizationId, providerRequest.SpendAuthorizationId);
        Assert.Equal(approved.AccountRevision, providerRequest.AccountRevision);
        Assert.Equal("wav", providerRequest.OutputFormat);
        Assert.True(providerRequest.ForceFresh);
    }

    [Fact]
    public async Task VoiceFingerprintDriftImmediatelyBeforeSubmissionFailsClosed()
    {
        var approved = CurrentEvidence();
        var changedSelection = approved.Selection with { VoiceFingerprint = "fingerprint-b" };
        var selectionReads = 0;
        var adapter = new RecordingAuditionAdapter(approved.Selection.ProviderStableId);
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(approved),
            (_, _) =>
            {
                selectionReads++;
                return Task.FromResult(changedSelection);
            },
            (_, _, _) => ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(adapter));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, selectionReads);
        Assert.Equal(0, adapter.SubmitCalls);
        Assert.Equal(1, adapter.DisposeCalls);
    }

    [Fact]
    public async Task EvidenceDriftAfterAdapterResolutionFailsClosedBeforeProviderSubmit()
    {
        var approved = CurrentEvidence();
        var changed = approved with { CredentialReferenceId = "credential-b" };
        var evidenceReads = 0;
        var adapter = new RecordingAuditionAdapter(approved.Selection.ProviderStableId);
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(++evidenceReads == 1 ? approved : changed),
            (_, _, _) => ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(adapter));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, evidenceReads);
        Assert.Equal(0, adapter.SubmitCalls);
        Assert.Equal(1, adapter.DisposeCalls);
    }

    [Fact]
    public async Task ChangedCredentialReferenceFailsClosedBeforeAdapterResolution()
    {
        var approved = CurrentEvidence();
        var current = approved with { CredentialReferenceId = "credential-b" };
        var resolveCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                resolveCalls++;
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(new RecordingAuditionAdapter(approved.Selection.ProviderStableId));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, resolveCalls);
    }

    [Fact]
    public async Task ChangedPricingEvidenceFailsClosedBeforeAdapterResolution()
    {
        var approved = CurrentEvidence();
        var current = approved with { PricingEvidenceId = "pricing-b" };
        var resolveCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                resolveCalls++;
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(new RecordingAuditionAdapter(approved.Selection.ProviderStableId));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, resolveCalls);
    }

    [Fact]
    public async Task ChangedSpendAuthorizationFailsClosedBeforeAdapterResolution()
    {
        var approved = CurrentEvidence();
        var current = approved with { SpendAuthorizationId = "spend-b" };
        var resolveCalls = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                resolveCalls++;
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(new RecordingAuditionAdapter(approved.Selection.ProviderStableId));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, resolveCalls);
    }

    [Fact]
    public async Task CacheEligibleRequestFailsClosedBeforeEvidenceOrAdapterResolution()
    {
        var approved = CurrentEvidence();
        var evidenceReads = 0;
        var adapterReads = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) =>
            {
                evidenceReads++;
                return Task.FromResult(approved);
            },
            (_, _, _) =>
            {
                adapterReads++;
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(new RecordingAuditionAdapter(approved.Selection.ProviderStableId));
            });
        var request = CurrentRequest() with { CachePolicyEligible = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, evidenceReads);
        Assert.Equal(0, adapterReads);
    }

    [Fact]
    public async Task NonFreshRequestFailsClosedBeforeEvidenceOrAdapterResolution()
    {
        var approved = CurrentEvidence();
        var evidenceReads = 0;
        var adapterReads = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) =>
            {
                evidenceReads++;
                return Task.FromResult(approved);
            },
            (_, _, _) =>
            {
                adapterReads++;
                return ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(new RecordingAuditionAdapter(approved.Selection.ProviderStableId));
            });
        var request = CurrentRequest() with { ForceFresh = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, evidenceReads);
        Assert.Equal(0, adapterReads);
    }

    [Fact]
    public async Task ProviderIdentityMismatchFailsClosedAndDisposesAdapter()
    {
        var approved = CurrentEvidence();
        var adapter = new RecordingAuditionAdapter("other-provider");
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(approved),
            (_, _, _) => ValueTask.FromResult<IVoiceLabAuditionProviderAdapter>(adapter));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(CurrentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, adapter.SubmitCalls);
        Assert.Equal(1, adapter.DisposeCalls);
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

    private static class CurrentRequestSelectionHolder
    {
        internal static readonly VoiceLabCatalogSelection Selection = CurrentSelection();
        internal static readonly VoiceLabAuditionRequest Request = new(
            Selection,
            CachePolicyEligible: false,
            ForceFresh: true,
            ExplicitSpendApproved: true,
            PricingCurrent: true,
            OutputFormat: "wav");
    }

    private sealed class RecordingAuditionAdapter : IVoiceLabAuditionProviderAdapter
    {
        internal RecordingAuditionAdapter(string providerStableId)
        {
            Descriptor = new ProviderDescriptor(providerStableId, "Voice Lab Test Provider", true, true);
        }

        public ProviderDescriptor Descriptor { get; }
        internal VoiceLabProviderAuditionRequest? LastRequest { get; private set; }
        internal int SubmitCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task<GenerationProviderResponse> SubmitVoiceLabAuditionAsync(
            VoiceLabProviderAuditionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
            SubmitCalls++;
            return Task.FromResult(Accepted());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
