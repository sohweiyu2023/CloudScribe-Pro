using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioRuntimeFactory
{
    /// <summary>Uses the normal exact-request admission, POST-once journal, capture and verified download path.
    /// Unadmitted children stop before submission; this method never expands installed authority.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One composition binds exact deferred evidence to serialized admission, capture and deterministic recovery without a second submission route.")]
    public async Task<GoogleLongAudioRecoveryRunner> CreateRecoveryRunnerAsync(GoogleLongAudioRequest root,
        GoogleLongAudioMediaTools tools, GoogleLongAudioOutputAllowance outputAllowance, bool captureConfirmed,
        IGoogleTrendHistoryHealth trendHealth, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(trendHealth);
        var context = await installed.LoadVerifiedAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Long Audio recovery requires the existing verified installation and ledger.");
        if (!context.Scope.ApprovedRequestCeilingsUsdCents.ContainsKey(root.BindingSha256))
            throw new InvalidOperationException("The root request is outside the installed Long Audio scope.");
        var logger = logs?.CreateLogger<GoogleLongAudioRecoveryRunner>();
        if (logger?.IsEnabled(LogLevel.Information) != true)
            throw new InvalidOperationException("Required recovery structural logging is unavailable. No request was sent.");
        trendHealth.RequireReady();
        var artifacts = new GoogleLongAudioRecoveryArtifactStore(context.OutputDirectory, new GoogleLongAudioFileJournal(context.OperationsDirectory));
        var recovery = new GoogleLongAudioRecoveryAuthority(root, context.OperationsDirectory, context.OutputDirectory);
        if (context.Scope.DeferredUnknownRanges is { Count: > 0 })
            recovery.Deferred = new(root, context.Scope, context.OperationsDirectory, context.OutputDirectory, context.LedgerPath, context.ScopeSha256, recovery);
        var replacements = new GoogleLongAudioFailedRangeReplacementResolver(root, context.Scope, context.OperationsDirectory, context.OutputDirectory);
        async Task<GoogleLongAudioJobProgress> Execute(GoogleLongAudioRequest request, bool pollOnly, CancellationToken cancel)
        {
            await RequireContextAsync(context, cancel).ConfigureAwait(false);
            trendHealth.RequireReady(); if (!pollOnly) GoogleLongAudioStructure.Log(logger, request, request.AttemptId == root.AttemptId ? 0 : -1); trendHealth.RequireReady();
            using var session = await OpenCoreAsync(request, tools, outputAllowance, captureConfirmed, recovery, cancel).ConfigureAwait(false);
            var outcome = pollOnly ? await session.PollAsync(cancel).ConfigureAwait(false) : await session.AdvanceAsync(cancel).ConfigureAwait(false);
            if (outcome.Artifact is { } artifact) await artifacts.RetainAsync(root, request, artifact, cancel).ConfigureAwait(false);
            return outcome;
        }
        async Task<GoogleLongAudioRecoveryAllowance> Allowance(GoogleLongAudioRequest parentRequest, CancellationToken cancel)
        {
            await RequireContextAsync(context, cancel).ConfigureAwait(false); trendHealth.RequireReady();
            long remaining = await RemainingRecoveryHoldAsync(context, cancel).ConfigureAwait(false);
            recovery.Deferred?.RequireReady(parentRequest, newPost: false);
            bool pending = recovery.Deferred is null && File.Exists(Path.Combine(context.OperationsDirectory, "observation-pending.guard"));
            var journal = new GoogleLongAudioFileJournal(context.OperationsDirectory);
            foreach (string path in Directory.EnumerateFiles(context.OperationsDirectory, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                    throw new InvalidDataException("Unexpected operation evidence requires reconciliation.");
                var saved = journal.Load(id);
                if (context.Scope.DeferredUnknownRanges?.Any(x => x.AttemptId == id) == true && recovery.Deferred is not null) continue;
                pending |= saved.State == GoogleLongAudioState.PostClaimed
                    || saved.OperationParseFailed || saved.Observation is not { ReadbackVerified: true, ReadState: CloudScribe.Domain.Generation.GoogleResponseReadState.Complete, Truncated: false };
                // Scan every operation for uncertainty, without rehashing unrelated audio on every split.
                // Execute verifies each reused child's own output, and final assembly verifies every included part.
            }
            bool accepted = File.Exists(Path.Combine(context.OutputDirectory, parentRequest.AttemptId.ToString("N") + ".verified.json"))
                || await artifacts.HasVerifiedAsync(parentRequest.AttemptId, parentRequest.BindingSha256, cancel).ConfigureAwait(false);
            return new(remaining, pending, accepted);
        }
        void Observe(GoogleLongAudioRecoveryTrace trace)
        {
            trendHealth.RequireReady();
            CloudScribeLog.LongAudioRecovery(logger, trace.RootAttemptId, trace.AttemptId, trace.ParentAttemptId, trace.StartScalar,
                trace.EndScalarExclusive, trace.SplitDepth, trace.PayloadSha256, trace.Stage, trace.FailureKind is { } kind ? (int)kind : null);
            trendHealth.RequireReady();
        }
        return new(Execute, Allowance, context.Scope.UsdPerMillionCharacters, clock, Observe,
            replacements.Resolve, replacements.IsReplacement, recovery.Deferred is { } deferred ? deferred.InspectDeferred : null);
    }

    private static async Task<long> RemainingRecoveryHoldAsync(InstalledGoogleLongAudioContext context, CancellationToken token)
    {
        var parent = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(await ChirpPolicyFiles.ReadAsync(context.ParentPolicyPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(await ChirpPolicyFiles.ReadAsync(context.LedgerPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        if (ledger.Version != 1 || !string.Equals(ledger.ScopeSha256, context.ScopeSha256, StringComparison.Ordinal) || ledger.Claims.Any(x => x.MaximumUsdCents <= 0))
            throw new InvalidDataException("The retained recovery ledger changed. Preserve all holds.");
        long reserved = 0; foreach (var claim in ledger.Claims) reserved = checked(reserved + claim.MaximumUsdCents);
        return checked(context.Scope.TotalGoogleCapUsdCents - context.Scope.TaxFxProvisionUsdCents - parent.Budget!.ProjectCeilingMinorUnits - reserved);
    }
}
