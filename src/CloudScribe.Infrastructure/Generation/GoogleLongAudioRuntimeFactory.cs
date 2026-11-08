using CloudScribe.Application.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Composition for an independently installed scope. Opening a session neither submits nor commits money.</summary>
public sealed partial class GoogleLongAudioRuntimeFactory(InstalledGoogleLongAudioStore installed,
    GoogleLongAudioAccountReadiness accounts, ITransientCredentialResolver credentials,
    IPrivateProviderErrorPersistence persistence, INativeMediaTool native, TimeProvider clock,
    Func<HttpMessageHandler>? trustedTransportFactory = null, ILoggerFactory? logs = null)
{
    public async Task<GoogleLongAudioRuntimeSession> OpenAsync(GoogleLongAudioRequest request,
        GoogleLongAudioMediaTools tools, GoogleLongAudioOutputAllowance readAllowance, bool protectedCaptureConfirmed,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(request, tools, readAllowance, protectedCaptureConfirmed, null, null, cancellationToken).ConfigureAwait(false);

    internal Task<GoogleLongAudioRuntimeSession> OpenDocumentAsync(GoogleLongAudioRequest request, GoogleLongAudioMediaTools tools,
        GoogleLongAudioOutputAllowance readAllowance, bool captureConfirmed, Func<CancellationToken, Task> freshSourceCheck, CancellationToken token) =>
        OpenCoreAsync(request, tools, readAllowance, captureConfirmed, null, (_, ct) => new ValueTask(freshSourceCheck(ct)), token);

    private async Task<GoogleLongAudioRuntimeSession> OpenCoreAsync(GoogleLongAudioRequest request,
        GoogleLongAudioMediaTools tools, GoogleLongAudioOutputAllowance readAllowance, bool protectedCaptureConfirmed,
        GoogleLongAudioRecoveryAuthority? recovery, Func<GoogleLongAudioRequest, CancellationToken, ValueTask>? freshSourceCheck, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(readAllowance);
        if (!protectedCaptureConfirmed) throw new InvalidOperationException("Private Long Audio job error capture requires the disclosed consent.");
        var context = await installed.LoadVerifiedAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Long Audio is not configured with a verified scope and preserved budget ledger.");
        if (context.Scope.ApprovedRequestCeilingsUsdCents is null
            || !context.Scope.ApprovedRequestCeilingsUsdCents.ContainsKey(request.BindingSha256) && recovery is null)
            throw new InvalidOperationException("This exact Long Audio request is outside the installed scope.");
        async ValueTask RequireCurrent(GoogleLongAudioRequest currentRequest, ChirpArticleAdmissionPolicy parent, CancellationToken token)
        {
            await RequireLocalReadyAsync(currentRequest, context, tools, readAllowance, token).ConfigureAwait(false);
        }
        var admission = new GoogleLongAudioLedgerAdmission(context.ParentPolicyPath, context.ParentPolicySha256,
            context.ScopePath, context.ScopeSha256, context.LedgerPath, RequireCurrent, clock).WithExtension(context.Extension).WithFreshSubmissionCheck(freshSourceCheck);
        if (recovery is not null) admission.WithRecovery(recovery);
        var review = await admission.ReviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (!review.IsAlreadyCommitted) recovery?.RequireNoOtherPending(request);
        await RequireLocalReadyAsync(request, context, tools, readAllowance, cancellationToken).ConfigureAwait(false);
        var observer = new GoogleLongAudioProtectedResponseObserver(persistence, clock);
        var journal = new GoogleLongAudioFileJournal(context.OperationsDirectory, recovery?.Deferred);
        var adapter = new GoogleLongAudioAdapter(credentials, admission, journal, observer, trustedTransportFactory?.Invoke(), logs?.CreateLogger<GoogleLongAudioAdapter>());
        GoogleLongAudioOutputBoundary? output = null;
        try
        {
            var authority = new GoogleLongAudioLedgerOutputAuthority(admission, journal, async (_, token) =>
            {
                await RequireContextAsync(context, token).ConfigureAwait(false);
                return readAllowance;
            });
            output = new(credentials, authority, journal, native, tools, context.OutputDirectory, clock, current =>
            {
                var capture = new GoogleLongAudioProtectedResponseObserver(persistence, clock);
                capture.ArmForJob(current, explicitlyConfirmed: true); return capture;
            }, trustedTransportFactory?.Invoke(), logs?.CreateLogger<GoogleLongAudioOutputBoundary>());
            return new(request, review, new(adapter, output), observer, adapter, output,
                token => new ValueTask(RequireLocalReadyAsync(request, context, tools, readAllowance, token)));
        }
        catch { output?.Dispose(); adapter.Dispose(); throw; }
    }

    private async Task RequireLocalReadyAsync(GoogleLongAudioRequest request, InstalledGoogleLongAudioContext context,
        GoogleLongAudioMediaTools tools, GoogleLongAudioOutputAllowance allowed, CancellationToken token)
    {
        string codec = string.Equals(request.AudioEncoding, "MP3", StringComparison.Ordinal) ? "mp3" : "pcm_s16le";
        if (allowed.MaximumBytes < 12 || allowed.SampleRate <= 0 || allowed.Channels <= 0
            || !string.Equals(allowed.ExpectedCodec, codec, StringComparison.Ordinal)
            || (allowed.ReadNotAfterUtc is { } deadline && deadline <= clock.GetUtcNow()))
            throw new InvalidOperationException("Correct the trusted local output settings before generating.");
        await GoogleLongAudioOutputBoundary.RequireToolAsync(tools.FfmpegPath, tools.FfmpegSha256, token).ConfigureAwait(false);
        await GoogleLongAudioOutputBoundary.RequireToolAsync(tools.FfprobePath, tools.FfprobeSha256, token).ConfigureAwait(false);
        var parent = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(await ChirpPolicyFiles.ReadAsync(context.ParentPolicyPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        await accounts.RequireCurrentAsync(request, parent.AccountId, parent.ProjectId, token).ConfigureAwait(false);
        await RequireContextAsync(context, token).ConfigureAwait(false);
    }

    private async ValueTask RequireContextAsync(InstalledGoogleLongAudioContext expected, CancellationToken token)
    {
        var current = await installed.LoadVerifiedAsync(token).ConfigureAwait(false);
        if (current is null || !string.Equals(current.ParentPolicySha256, expected.ParentPolicySha256, StringComparison.Ordinal)
            || !string.Equals(current.ScopeSha256, expected.ScopeSha256, StringComparison.Ordinal)
            || !string.Equals(current.LedgerPath, expected.LedgerPath, StringComparison.Ordinal)
            || !string.Equals(current.OperationsDirectory, expected.OperationsDirectory, StringComparison.Ordinal)
            || !string.Equals(current.OutputDirectory, expected.OutputDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("Installed Long Audio identity changed; saved operations and commitments must be reconciled.");
    }
}
