using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("payload")]
    [InlineData("credential")]
    [InlineData("account")]
    [InlineData("parent-state")]
    [InlineData("bound")]
    [InlineData("budget")]
    [InlineData("expiry")]
    public async Task DiagnosticSuccessorRejectsChangedEvidenceAndLimits(string mismatch)
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var state = await CreateSuccessorFixtureAsync(fixture);
        var policy = state.Evidence.Admission.Policy!; var intent = state.Intent;
        if (string.Equals(mismatch, "payload", StringComparison.Ordinal)) intent = intent with { CompiledPayload = "changed"u8.ToArray() };
        if (string.Equals(mismatch, "credential", StringComparison.Ordinal)) intent = intent with { Envelope = intent.Envelope with { CredentialReferenceId = "different" } };
        if (string.Equals(mismatch, "account", StringComparison.Ordinal)) intent = intent with { Envelope = intent.Envelope with { AccountId = "different" } };
        state.Evidence.Admission.Policy = mismatch switch
        {
            "parent-state" => policy with { SuccessorGrant = policy.SuccessorGrant! with { ParentQueueRowSha256 = new string('f', 64) } },
            "bound" => policy with { SuccessorGrant = policy.SuccessorGrant! with { MaximumNewAttempts = 2 } },
            "budget" => policy with { Budget = policy.Budget with { ProjectCeilingMinorUnits = 149 } },
            "expiry" => policy with { ExpiresAtUtc = state.Evidence.Clock.Now.AddSeconds(-1) },
            _ => policy,
        };
        await Assert.ThrowsAnyAsync<Exception>(() => state.Evidence.Service.PrepareAsync(intent, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(fixture, "chirp_article_successor_attempts"));
        Assert.Equal(state.ParentRowsSha256, JsonHash(await FinancialRowsAsync(fixture, "chirp_article_attempts")));
    }

    [Fact]
    public async Task SuccessorClaimAllowsOneOwnerAndNeverResetsHistoryOrRegistry()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var state = await CreateSuccessorFixtureAsync(fixture);
        var review = await state.Evidence.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        var child = await state.Evidence.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var envelope = state.Intent.Envelope with { ChirpAuthorizationId = child.AuthorizationId };
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => state.Evidence.Service.TryClaimAuthorizedAsync(envelope,
            state.Intent.ProjectId, "USD", 2, 1, ChirpClaim(state.Intent, child), TestContext.Current.CancellationToken)));
        Assert.Single(claims, value => value);
        using var db = fixture.CreateDbContext();
        foreach (string sql in new[]
        {
            "DELETE FROM chirp_article_attempts", "DELETE FROM chirp_article_successor_attempts", "DELETE FROM chirp_article_authorizations",
            "UPDATE chirp_article_successor_attempts SET Consumed=0", "UPDATE chirp_article_successor_attempts SET ClaimedLocalAttemptId=NULL",
            "UPDATE chirp_article_successor_attempts SET SourceApprovalSha256='changed'",
            "INSERT INTO chirp_article_authorizations VALUES('unowned')",
        })
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken));
        Assert.Equal(state.ParentRowsSha256, JsonHash(await FinancialRowsAsync(fixture, "chirp_article_attempts")));
    }
}
