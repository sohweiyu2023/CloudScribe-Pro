using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    public async Task LongAudioReadContinuationRequiresOriginalClaimAndNeverAddsMoney(bool committed, long ceiling)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        var prepared = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock, requestCeiling: ceiling);
        if (committed) await prepared.Admission.RequireSubmissionAuthorizedAsync(prepared.Request, TestContext.Current.CancellationToken);
        byte[] original = File.ReadAllBytes(prepared.LedgerPath);
        var estimate = await prepared.Admission.ReviewAsync(prepared.Request, TestContext.Current.CancellationToken);
        Assert.Equal(1, estimate.EstimatedSynthesisUsdCents);
        Assert.Equal(committed, estimate.IsAlreadyCommitted);
        Assert.Equal(committed ? 0 : ceiling, estimate.AdditionalCommitmentUsdCents);
        Assert.Equal(714 + (committed ? ceiling : 0), estimate.RetainedCommitmentsUsdCents);
        Assert.Equal(914 + ceiling, estimate.ProjectedProvisionUsdCents);
        Assert.Equal(original, File.ReadAllBytes(prepared.LedgerPath));
        if (committed)
        {
            await prepared.Admission.RequireExistingCommitmentAsync(prepared.Request, TestContext.Current.CancellationToken);
            await prepared.Admission.RequireExistingCommitmentAsync(prepared.Request, TestContext.Current.CancellationToken);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.Admission.RequireExistingCommitmentAsync(prepared.Request, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(original, File.ReadAllBytes(prepared.LedgerPath));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("uncommitted")]
    [InlineData("pending")]
    [InlineData("changed")]
    [InlineData("capture-pending")]
    public async Task LongAudioOutputAuthorityBindsDurableCompletionAndRetainedMoney(string state)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        var p = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock);
        if (state is not "uncommitted") await p.Admission.RequireSubmissionAuthorizedAsync(p.Request, TestContext.Current.CancellationToken);
        string journalRoot = Path.Combine(f.Root, "synthetic-long-journal");
        var journal = new GoogleLongAudioFileJournal(journalRoot);
        var done = new GoogleLongAudioCheckpoint(p.Request.AttemptId, p.Request.BindingSha256, p.Request.ProjectNumber,
            p.Request.Location, p.Request.CredentialReferenceId, p.Request.OutputGcsUri, p.Request.PayloadSha256, p.Request.PayloadBytes,
            state is "pending" ? GoogleLongAudioState.Pollable : GoogleLongAudioState.CompletedUnverified,
            "projects/123456/locations/global/operations/synthetic", 200, null, 1)
        {
            Observation = new(Guid.NewGuid(), CloudScribe.Domain.Generation.GoogleResponseReadState.Complete,
                GoogleLongAudioParseState.Json, GoogleErrorStatus.NotProvided, 64,
                GoogleExplanationRetentionState.SavedPrivately, state is not "capture-pending", false, false)
        };
        await File.WriteAllBytesAsync(Path.Combine(journalRoot, p.Request.AttemptId.ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(done), TestContext.Current.CancellationToken);
        if (state is "changed") done = done with { PayloadBytes = done.PayloadBytes + 1 };
        int reads = 0;
        var authority = new GoogleLongAudioLedgerOutputAuthority(p.Admission, journal, (_, _) =>
        { reads++; return ValueTask.FromResult(new GoogleLongAudioOutputAllowance(4096, null, "mp3", 24000, 1)); });
        byte[] before = File.ReadAllBytes(p.LedgerPath);
        if (state is "completed")
        {
            var allowed = await authority.RequireReadAuthorizedAsync(p.Request, done, TestContext.Current.CancellationToken);
            Assert.Null(allowed.ReadNotAfterUtc); Assert.Equal(1, reads);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => authority.RequireReadAuthorizedAsync(p.Request, done, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, reads);
        }
        Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
    }

    [Fact]
    public async Task LongAudioAdmissionRetainsWholeParentProvisionAndRejectsRepeatedSubmissionCommitment()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        var prepared = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock);
        byte[] parentBefore = File.ReadAllBytes(prepared.ParentPath);
        await prepared.Admission.RequireSubmissionAuthorizedAsync(prepared.Request, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.Admission.RequireSubmissionAuthorizedAsync(prepared.Request, TestContext.Current.CancellationToken).AsTask());
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(prepared.LedgerPath))!;
        var claim = Assert.Single(ledger.Claims); Assert.Equal(1, claim.MaximumUsdCents);
        Assert.Equal(prepared.Request.BindingSha256, claim.RequestBindingSha256);
        Assert.Equal(parentBefore, File.ReadAllBytes(prepared.ParentPath));
    }

    [Theory]
    [InlineData("missing-ledger")]
    [InlineData("corrupt-ledger")]
    [InlineData("scope-pin")]
    [InlineData("parent-pin")]
    [InlineData("cap")]
    [InlineData("unapproved-request")]
    public async Task LongAudioAdmissionFailsClosedWithoutReinitializingMoney(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        var prepared = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock, cap: fault is "cap" ? 914 : 1000);
        byte[] original = File.ReadAllBytes(prepared.LedgerPath);
        switch (fault)
        {
            case "missing-ledger": File.Delete(prepared.LedgerPath); break;
            case "corrupt-ledger": File.WriteAllText(prepared.LedgerPath, "{partial"); break;
            case "scope-pin": File.AppendAllText(prepared.ScopePath, " "); break;
            case "parent-pin": File.AppendAllText(prepared.ParentPath, " "); break;
        }
        var request = fault is "unapproved-request" ? LongLedgerRequest() : prepared.Request;
        await Assert.ThrowsAnyAsync<Exception>(() => prepared.Admission.RequireSubmissionAuthorizedAsync(request, TestContext.Current.CancellationToken).AsTask());
        if (fault is "missing-ledger") Assert.False(File.Exists(prepared.LedgerPath));
        else if (fault is not "corrupt-ledger") Assert.Equal(original, File.ReadAllBytes(prepared.LedgerPath));
    }

    [Fact]
    public async Task LongAudioAdmissionRechecksScopeAfterReadinessAndDoesNotSpendWhenDenied()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var evidence = await ChirpEvidenceAsync(f);
        string? scopePath = null;
        var prepared = await LongLedgerFixtureAsync(f, evidence.Admission.Policy!, evidence.Clock, readiness: (_, _, _) =>
        { File.AppendAllText(scopePath!, " "); return ValueTask.CompletedTask; });
        scopePath = prepared.ScopePath;
        byte[] original = File.ReadAllBytes(prepared.LedgerPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.Admission.RequireSubmissionAuthorizedAsync(prepared.Request, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(original, File.ReadAllBytes(prepared.LedgerPath));
    }

    private static GoogleLongAudioRequest LongLedgerRequest()
    {
        Guid id = Guid.NewGuid();
        return GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-credential",
            "gs://synthetic-private/output/" + id.ToString("N") + ".mp3", "Synthetic text.", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3");
    }

    private static async Task<(GoogleLongAudioLedgerAdmission Admission, GoogleLongAudioRequest Request, string ParentPath,
        string ScopePath, string LedgerPath)> LongLedgerFixtureAsync(Fixture f, ChirpArticleAdmissionPolicy original, TimeProvider clock,
        long cap = 1000, Func<GoogleLongAudioRequest, ChirpArticleAdmissionPolicy, CancellationToken, ValueTask>? readiness = null, long requestCeiling = 1)
    {
        string root = Path.Combine(f.Root, "long-ledger-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var parent = original with { Budget = original.Budget with { ProjectCommittedMinorUnits = 553, ProjectCeilingMinorUnits = 714 } };
        string parentPath = Path.Combine(root, "parent.json"), scopePath = Path.Combine(root, "scope.json"), ledgerPath = Path.Combine(root, "ledger.json");
        byte[] parentBytes = JsonSerializer.SerializeToUtf8Bytes(parent); string parentSha = Convert.ToHexStringLower(SHA256.HashData(parentBytes));
        var request = LongLedgerRequest();
        var scope = new GoogleLongAudioBudgetScope(1, parentSha, new string('d', 64), request.ProjectNumber, request.CredentialReferenceId,
            request.VoiceName, request.LanguageCode, request.AudioEncoding, "synthetic-private", cap, 200, 30m, "synthetic pricing fixture",
            new Dictionary<string, long>(StringComparer.Ordinal) { [request.BindingSha256] = requestCeiling });
        byte[] scopeBytes = JsonSerializer.SerializeToUtf8Bytes(scope); string scopeSha = Convert.ToHexStringLower(SHA256.HashData(scopeBytes));
        await File.WriteAllBytesAsync(parentPath, parentBytes, TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(scopePath, scopeBytes, TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(ledgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, scopeSha, [])), TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (new(parentPath, parentSha, scopePath, scopeSha, ledgerPath, readiness ?? ((_, _, _) => ValueTask.CompletedTask), clock), request, parentPath, scopePath, ledgerPath);
    }
}
