using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Inactive until installed with exact reviewed scope/parent pins and real account/storage readiness.
/// This supplements, never retires or consumes, the synchronous grant. Its entire ceiling remains provisioned.</summary>
public sealed partial class GoogleLongAudioLedgerAdmission : IGoogleLongAudioAdmission, IGoogleLongAudioSubmissionPreflight
{
    private readonly string parentPath, parentSha, scopePath, scopeSha, ledgerPath;
    private readonly Func<GoogleLongAudioRequest, ChirpArticleAdmissionPolicy, CancellationToken, ValueTask> requireReadiness;
    private readonly TimeProvider clock;
    private GoogleLongAudioRecoveryAuthority? recoveryAuthority;

    private IGoogleLongAudioScopeExtensionSource? extensionSource;
    private GoogleLongAudioScopeExtension extensionInEffect = GoogleLongAudioScopeExtension.Empty;

    internal GoogleLongAudioLedgerAdmission WithRecovery(GoogleLongAudioRecoveryAuthority authority)
    {
        recoveryAuthority = authority; return this;
    }

    /// <summary>Includes batch-approved jobs from <paramref name="source"/>. The installed scope, cap and ledger still govern.</summary>
    public GoogleLongAudioLedgerAdmission WithExtension(IGoogleLongAudioScopeExtensionSource? source)
    {
        extensionSource = source; return this;
    }

    public GoogleLongAudioLedgerAdmission(string parentPolicyPath, string parentPolicySha256, string reviewedScopePath,
        string reviewedScopeSha256, string existingLedgerPath,
        Func<GoogleLongAudioRequest, ChirpArticleAdmissionPolicy, CancellationToken, ValueTask> requireCurrentReadiness, TimeProvider? timeProvider = null)
    {
        parentPath = RequirePath(parentPolicyPath); parentSha = RequireHash(parentPolicySha256);
        scopePath = RequirePath(reviewedScopePath); scopeSha = RequireHash(reviewedScopeSha256);
        ledgerPath = RequirePath(existingLedgerPath);
        if (new[] { parentPath, scopePath, ledgerPath }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("Private scope, parent and ledger must be distinct physical files.", nameof(existingLedgerPath));
        requireReadiness = requireCurrentReadiness ?? throw new ArgumentNullException(nameof(requireCurrentReadiness));
        clock = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask RequireSubmissionAuthorizedAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, parent, amount, proof) = await ReadScopeAsync(request, cancellationToken).ConfigureAwait(false);
        recoveryAuthority?.RequireNoOtherPending(request);
        ChirpPolicyFiles.RejectLinks(ledgerPath + ".lock");
        using var lease = new FileStream(ledgerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var original = await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(original);
        ValidateLedger(ledger);
        long previous = 0;
        foreach (var claim in ledger.Claims) previous = checked(previous + claim.MaximumUsdCents);
        var prior = ledger.Claims.SingleOrDefault(x => x.AttemptId == request.AttemptId);
        if (prior is not null)
        {
            if (!Same(prior.RequestBindingSha256, request.BindingSha256) || prior.MaximumUsdCents != amount || prior.Recovery != proof)
                throw new InvalidDataException("Existing Long Audio commitment cannot be rebound.");
            throw new InvalidOperationException("This Long Audio request is already committed; reconcile its original operation journal without resubmitting.");
        }
        RequireBudget(scope, parent, checked(previous + amount));
        RequireFamilyLimit(ledger, request, proof, amount);
        // Only structurally valid, unconsumed financial authority may reach credential/storage readiness.
        await requireReadiness(request, parent, cancellationToken).ConfigureAwait(false);
        var revalidated = await ReadScopeAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidated.Ceiling != amount || revalidated.Proof != proof)
            throw new InvalidDataException("Long Audio request authority changed during current readiness.");
        var next = ledger with { Claims = [.. ledger.Claims, new(request.AttemptId, request.BindingSha256, amount, proof)] };
        string pending = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(next)); file.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            ChirpPolicyFiles.RejectLinks(ledgerPath);
            byte[] current = await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!original.AsSpan().SequenceEqual(current))
                throw new InvalidDataException("Long Audio commitments changed during admission.");
            File.Move(pending, ledgerPath, overwrite: true);
        }
        finally { if (File.Exists(pending)) { ChirpPolicyFiles.RejectLinks(pending); File.Delete(pending); } }
    }

    /// <summary>Exact current estimate; never creates a financial claim or a submission journal.</summary>
    public async ValueTask<GoogleLongAudioCostReview> ReviewAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
    {
        var (scope, parent, amount, proof) = await ReadScopeAsync(request, cancellationToken).ConfigureAwait(false);
        ChirpPolicyFiles.RejectLinks(ledgerPath + ".lock");
        using var lease = new FileStream(ledgerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        ValidateLedger(ledger);
        long provision = 0;
        foreach (var claim in ledger.Claims) provision = checked(provision + claim.MaximumUsdCents);
        RequireBudget(scope, parent, provision);
        var existing = ledger.Claims.SingleOrDefault(x => x.AttemptId == request.AttemptId);
        if (existing is not null && (!Same(existing.RequestBindingSha256, request.BindingSha256) || existing.MaximumUsdCents != amount
            || existing.Recovery != proof))
            throw new InvalidDataException("The saved Long Audio commitment cannot be presented as a different request.");
        long retained = checked(parent.Budget.ProjectCeilingMinorUnits + provision);
        long additional = existing is null ? amount : 0;
        var review = GoogleLongAudioCostReview.Prepare(request, scope.UsdPerMillionCharacters,
            retained, scope.TaxFxProvisionUsdCents, scope.TotalGoogleCapUsdCents, scope.PricingProvenance);
        RequireBudget(scope, parent, checked(provision + additional));
        return review with
        {
            IsAlreadyCommitted = existing is not null,
            AdditionalCommitmentUsdCents = additional,
            ProjectedProvisionUsdCents = checked(retained + scope.TaxFxProvisionUsdCents + additional)
        };
    }

    async ValueTask IGoogleLongAudioSubmissionPreflight.RequireBeforeJournalAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
    {
        var review = await ReviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (review.IsAlreadyCommitted)
            throw new InvalidOperationException("This request already has a retained commitment; reconcile its saved operation instead of submitting it.");
    }

    /// <summary>Read-only continuation check. It neither creates a commitment nor authorizes another POST.</summary>
    public async ValueTask RequireExistingCommitmentAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, parent, amount, proof) = await ReadScopeAsync(request, cancellationToken).ConfigureAwait(false);
        ChirpPolicyFiles.RejectLinks(ledgerPath + ".lock");
        using var lease = new FileStream(ledgerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[] original = await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(original);
        ValidateLedger(ledger);
        var claim = ledger.Claims.SingleOrDefault(x => x.AttemptId == request.AttemptId);
        if (claim is null || !Same(claim.RequestBindingSha256, request.BindingSha256)
            || claim.MaximumUsdCents != amount || claim.Recovery != proof)
            throw new InvalidOperationException("The original Long Audio commitment is absent or changed; no storage read is authorized.");
        long provision = 0;
        foreach (var item in ledger.Claims) provision = checked(provision + item.MaximumUsdCents);
        RequireBudget(scope, parent, provision);
        await requireReadiness(request, parent, cancellationToken).ConfigureAwait(false);
        var revalidated = await ReadScopeAsync(request, cancellationToken).ConfigureAwait(false);
        if (revalidated.Ceiling != amount || revalidated.Proof != proof)
            throw new InvalidDataException("Long Audio continuation authority changed during current readiness.");
        byte[] current = await ChirpPolicyFiles.ReadAsync(ledgerPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!original.AsSpan().SequenceEqual(current))
            throw new InvalidDataException("Long Audio history changed during continuation verification.");
    }

    /// <summary>A batch-approved file may never commit more than its approved maximum, including adaptive split requests.</summary>
    private void RequireFamilyLimit(GoogleLongAudioBudgetLedger ledger, GoogleLongAudioRequest request, GoogleLongAudioRecoveryProvenance? proof, long amount)
    {
        string root = proof?.RootBindingSha256 ?? request.BindingSha256;
        if (extensionInEffect.ForRoot(root) is not { } job) return;
        long family = amount;
        foreach (var claim in ledger.Claims.Where(x => Same(x.RequestBindingSha256, root) || x.Recovery is { } r && Same(r.RootBindingSha256, root)))
            family = checked(family + claim.MaximumUsdCents);
        if (family > job.FamilyMaximumUsdCents)
            throw new GoogleLongAudioBudgetExceededException(family, job.FamilyMaximumUsdCents);
    }

    private static void RequireBudget(GoogleLongAudioBudgetScope scope, ChirpArticleAdmissionPolicy parent, long longAudioCommitments)
    {
        long total = checked(parent.Budget.ProjectCeilingMinorUnits + scope.TaxFxProvisionUsdCents + longAudioCommitments);
        if (total > scope.TotalGoogleCapUsdCents)
            throw new GoogleLongAudioBudgetExceededException(total, scope.TotalGoogleCapUsdCents);
    }

    private void ValidateLedger(GoogleLongAudioBudgetLedger ledger)
    {
        if (ledger.Version != 1 || !Same(ledger.ScopeSha256, scopeSha) || ledger.Claims is null
            || ledger.Claims.Any(x => x is null || x.AttemptId == Guid.Empty || !IsHash(x.RequestBindingSha256) || x.MaximumUsdCents <= 0)
            || ledger.Claims.Any(x => x.Recovery is { } proof && (!IsHash(proof.RootBindingSha256) || !IsHash(proof.ParentBindingSha256)
                || !IsHash(proof.ParentCheckpointSha256) || proof.StartScalar < 0 || proof.EndScalarExclusive <= proof.StartScalar
                || !Same(proof.PlannerVersion, GoogleLongAudioRecoveryPlanner.Version)
                    && !Same(proof.PlannerVersion, GoogleLongAudioExplicitRegeneration.PlannerVersion)))
            || ledger.Claims.Select(x => x.AttemptId).Distinct().Count() != ledger.Claims.Count
            || ledger.Claims.Select(x => x.RequestBindingSha256).Distinct(StringComparer.Ordinal).Count() != ledger.Claims.Count)
            throw new InvalidDataException("Long Audio cumulative ledger is invalid; history must be reconciled, never reset.");
    }

    private static string RequirePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute reviewed private file path is required.", nameof(path));
        string full = Path.GetFullPath(path); ChirpPolicyFiles.RejectLinks(full); return full;
    }
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static string RequireHash(string hash) => IsHash(hash) ? hash : throw new ArgumentException("An exact reviewed SHA256 pin is required.", nameof(hash));
}
