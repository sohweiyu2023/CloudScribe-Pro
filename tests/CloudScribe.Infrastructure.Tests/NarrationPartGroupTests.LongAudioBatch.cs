#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

/// <summary>Batch approvals against the real Long Audio ledger admission (synthetic files; no provider is contacted).</summary>
public sealed partial class NarrationPartGroupTests
{
    private sealed class BatchClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One end-to-end scenario through the real ledger.")]
    public async Task BatchApprovalAdmitsLongAudioFilesOnlyWithinTheInstalledCapAndKeepsCommittedJobs()
    {
        var token = TestContext.Current.CancellationToken;
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        // Installed: cap 1000¢, tax/FX 200¢, project ceiling 714¢, one installed request of 1¢ → 85¢ of room for batch files.
        var (installed, installedRequest, _, scopePath, ledgerPath) = await LongLedgerFixtureAsync(f, e.Admission.Policy!, e.Clock);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(await File.ReadAllBytesAsync(scopePath, token))!;
        var clock = new BatchClock(e.Clock.GetUtcNow());
        string profileRoot = Path.Combine(f.Root, "batch-profile-root"), install = Path.Combine(f.Root, "batch-install"), app = Path.Combine(install, "app");
        Directory.CreateDirectory(app); Directory.CreateDirectory(Path.Combine(install, "batch-authority"));
        var profile = new BatchAuthorityProfile(BatchAuthorityProfile.CurrentSchemaVersion, BatchAuthorityMode.InAppWithinLimits,
            InstalledRecoveryAdmissionStore.ProfileHash(profileRoot), "acct", "proj", [], [BatchEngines.LongAudio, BatchEngines.CompleteArticle],
            BatchFailurePolicy.SkipAndContinue, "USD", 2, 5000, 10000, 30, 500, TimeSpan.FromDays(2), clock.Now.AddDays(60), "SGD", 1.30m, 3000);
        byte[] profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, InstalledBatchAuthorityStore.Json);
        await File.WriteAllBytesAsync(Path.Combine(install, "batch-authority", "profile.json"), profileBytes, token);
        await File.WriteAllTextAsync(Path.Combine(install, "batch-authority", "pins.json"),
            JsonSerializer.Serialize(new BatchAuthorityPins(1, Convert.ToHexStringLower(SHA256.HashData(profileBytes)), new Dictionary<string, string>(StringComparer.Ordinal))), token);
        var store = new InstalledBatchAuthorityStore(app, profileRoot);
        var source = new BatchLongAudioScopeExtensionSource(store, clock);
        var settings = new BatchSettings("cmn-CN", scope.VoiceName, "Alnilam", BatchEngines.LongAudioFirst, BatchFailurePolicy.SkipAndContinue);

        (GoogleLongAudioRequest Request, BatchConsent Consent) Approve(string text, long? ceilingOverride = null)
        {
            var batch = new NarrationBatch(1, Guid.NewGuid(), "b", 1, BatchQueueState.Ready, settings, settings.Sha256(), "out", "USD", 2, [], clock.Now);
            var attempt = Guid.NewGuid();
            var request = GoogleLongAudioRequest.Create(attempt, scope.ProjectNumber, "global", scope.CredentialReferenceId,
                $"gs://{scope.OutputBucket}/cloudscribe-batch/{batch.BatchId:N}/{attempt:N}.mp3", text, scope.LanguageCode, scope.VoiceName, scope.AudioEncoding);
            long ceiling = ceilingOverride ?? Math.Max(1, (long)decimal.Ceiling(request.ConservativeCharacterUnits * scope.UsdPerMillionCharacters / 10_000m));
            var approval = new BatchLongAudioApproval(attempt, request.OutputGcsUri, request.BindingSha256, ceiling, ceiling, 4096, 24000, 1, false);
            var item = new BatchConsentItem(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), Guid.NewGuid(), Guid.NewGuid(),
                request.BindingSha256, ceiling, BatchEngines.LongAudio, approval);
            return (request, BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.InApp, profile, "acct", "proj", [item], clock.Now));
        }

        var (first, firstConsent) = Approve("第一個批次檔案。");
        // Without a batch approval the request is outside the installed scope.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await installed.ReviewAsync(first, token));
        await store.SaveInAppConsentAsync(firstConsent, token);
        var admission = installed.WithExtension(source);
        var review = await admission.ReviewAsync(first, token);
        Assert.False(review.IsAlreadyCommitted);
        Assert.True(review.EstimatedSynthesisUsdCents <= firstConsent.Items[0].LongAudio!.CeilingUsdCents);
        _ = await admission.ReviewAsync(installedRequest, token); // Installed jobs keep working unchanged.

        // An approval bigger than the room left in the installed cap is never admitted, and does not disturb the others.
        var (tooBig, tooBigConsent) = Approve("太大的批次檔案。", ceilingOverride: 500);
        await store.SaveInAppConsentAsync(tooBigConsent, token);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await admission.ReviewAsync(tooBig, token));
        _ = await admission.ReviewAsync(first, token);
        _ = await admission.ReviewAsync(installedRequest, token);

        // Commit the first file through the real ledger admission.
        await admission.RequireSubmissionAuthorizedAsync(first, token);
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(await File.ReadAllBytesAsync(ledgerPath, token))!;
        Assert.Contains(ledger.Claims, x => x.AttemptId == first.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await admission.RequireSubmissionAuthorizedAsync(first, token)); // never twice

        // After every approval expires, the committed file is still admitted (so it can be checked and downloaded);
        // an uncommitted one is not.
        var (unstarted, unstartedConsent) = Approve("尚未開始的檔案。");
        await store.SaveInAppConsentAsync(unstartedConsent, token);
        _ = await admission.ReviewAsync(unstarted, token);
        clock.Now = clock.Now.AddDays(3);
        Assert.True((await admission.ReviewAsync(first, token)).IsAlreadyCommitted);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await admission.ReviewAsync(unstarted, token));
        await admission.RequireExistingCommitmentAsync(first, token);
    }

    [Fact]
    public async Task BatchScopeExtensionNeverDisablesInstalledLongAudio()
    {
        var token = TestContext.Current.CancellationToken;
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        var plain = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(token);
        var withBroken = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock, new ThrowingExtension()).LoadVerifiedAsync(token);
        var withColliding = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock, new CollidingExtension()).LoadVerifiedAsync(token);
        Assert.NotNull(plain); Assert.NotNull(withBroken); Assert.NotNull(withColliding);
        Assert.Equal(plain.Scope.ApprovedRequestCeilingsUsdCents!.Count, withBroken.Scope.ApprovedRequestCeilingsUsdCents!.Count);
        Assert.Equal(plain.Scope.ApprovedRequestCeilingsUsdCents.Count, withColliding.Scope.ApprovedRequestCeilingsUsdCents!.Count);
        Assert.Equal(plain.ScopeSha256, withBroken.ScopeSha256); // The pinned identity is the installed scope, never the extension.
    }

    private sealed class ThrowingExtension : IGoogleLongAudioScopeExtensionSource
    {
        public ValueTask<GoogleLongAudioScopeExtension> LoadAsync(GoogleLongAudioBudgetScope scope, string scopeSha256, string ledgerPath,
            long fixedCommitmentsUsdCents, CancellationToken cancellationToken) => throw new InvalidDataException("synthetic failure");
    }

    /// <summary>Claims an installed binding: Apply must refuse it rather than replace installed authority.</summary>
    private sealed class CollidingExtension : IGoogleLongAudioScopeExtensionSource
    {
        public ValueTask<GoogleLongAudioScopeExtension> LoadAsync(GoogleLongAudioBudgetScope scope, string scopeSha256, string ledgerPath,
            long fixedCommitmentsUsdCents, CancellationToken cancellationToken)
        {
            string binding = scope.ApprovedRequestCeilingsUsdCents!.Keys.First();
            return ValueTask.FromResult(new GoogleLongAudioScopeExtension([new GoogleLongAudioBatchJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('a', 64), Guid.NewGuid(), "gs://synthetic-private/x/" + Guid.NewGuid().ToString("N") + ".mp3", 4096, 24000, 1, binding, 999_999, 999_999, false)]));
        }
    }
}
