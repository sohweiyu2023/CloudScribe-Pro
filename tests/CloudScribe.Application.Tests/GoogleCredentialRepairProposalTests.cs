using CloudScribe.Application.Security;

namespace CloudScribe.Application.Tests;

public sealed class GoogleCredentialRepairProposalTests
{
    private static readonly Guid RepairId = Guid.Parse("5ea0a6d4-6858-4104-9df6-04fb3d280031");
    private static readonly GoogleCredentialRepairPresence Absent = new(
        CredentialRolePresence.Absent, CredentialRolePresence.Absent, CredentialRolePresence.Absent);
    private static string Digest(char value) => new(value, 64);
    private static GoogleCredentialRepairScope Scope() => new(
        "synthetic-account", "synthetic-project", "synthetic-original-key", 7,
        Digest('a'), Digest('b'), Digest('c'), Digest('d'), Digest('e'));

    [Fact]
    public void MatchingAbsentRolesProduceOnlyADistinctOfflineProposal()
    {
        var scope = Scope();
        var proposal = GoogleCredentialRepairProposalBuilder.Prepare(scope, scope, Absent, Digest('e'), RepairId);
        Assert.Equal(scope, proposal.Scope);
        Assert.Equal("synthetic-original-key", proposal.Scope.ExistingCredentialReference);
        Assert.Equal("google-repair.5ea0a6d4685841049df604fb3d280031", proposal.ProposedCredentialReference);
        Assert.NotEqual(scope.ExistingCredentialReference, proposal.ProposedCredentialReference, StringComparer.Ordinal);
        Assert.Equal(RepairId, proposal.RepairId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AnyPresentOrUnknownRoleBlocksProposal(int role)
    {
        foreach (var state in new[] { CredentialRolePresence.Unknown, CredentialRolePresence.Present, (CredentialRolePresence)99 })
        {
            var presence = role switch
            {
                0 => Absent with { Marker = state },
                1 => Absent with { Metadata = state },
                _ => Absent with { PrivateKey = state },
            };
            Assert.Throws<InvalidOperationException>(() => GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), presence, Digest('e'), RepairId));
        }
    }

    [Fact]
    public void EveryChangedAccountProfileBuildEndpointOrIdentityBindingBlocksProposal()
    {
        var expected = Scope();
        var changed = new[]
        {
            expected with { AccountId = "other-account" }, expected with { ProjectId = "other-project" },
            expected with { ExistingCredentialReference = "other-key" }, expected with { AccountRevision = 8 },
            expected with { FullAccountRowSha256 = Digest('f') }, expected with { EndpointBindingSha256 = Digest('f') },
            expected with { ProfileSha256 = Digest('f') }, expected with { BuildSha256 = Digest('f') },
            expected with { OriginalIdentityAnchorSha256 = Digest('f') },
        };
        foreach (var current in changed)
            Assert.Throws<InvalidOperationException>(() => GoogleCredentialRepairProposalBuilder.Prepare(expected, current, Absent, Digest('e'), RepairId));
    }

    [Fact]
    public void SuppliedCandidateCannotEstablishItsOwnOriginalIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, Digest('f'), RepairId));
        var missing = Scope() with { OriginalIdentityAnchorSha256 = string.Empty };
        Assert.Throws<InvalidDataException>(() => GoogleCredentialRepairProposalBuilder.Prepare(missing, missing, Absent, Digest('e'), RepairId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidOrNonCanonicalCandidateDigestIsRejected(string digest)
    {
        Assert.Throws<InvalidDataException>(() => GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, digest, RepairId));
    }

    [Fact]
    public void EmptyRepairIdAndCollisionWithOriginalTargetAreRejected()
    {
        Assert.Throws<ArgumentException>(() => GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, Digest('e'), Guid.Empty));
        var scope = Scope() with { ExistingCredentialReference = $"google-repair.{RepairId:N}" };
        Assert.Throws<InvalidOperationException>(() => GoogleCredentialRepairProposalBuilder.Prepare(scope, scope, Absent, Digest('e'), RepairId));
    }

    [Fact]
    public void DistinctRepairSessionsHaveDistinctStagingNamespaces()
    {
        var first = GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, Digest('e'), RepairId);
        var repeated = GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, Digest('e'), RepairId);
        var other = GoogleCredentialRepairProposalBuilder.Prepare(Scope(), Scope(), Absent, Digest('e'), Guid.Parse("2b807a18-15b7-43d9-b4de-0374edfd9062"));
        Assert.Equal(first, repeated);
        Assert.NotEqual(first.ProposedCredentialReference, other.ProposedCredentialReference, StringComparer.Ordinal);
        Assert.Equal(first.Scope, other.Scope);
    }

    [Fact]
    public void NormalizedAliasesCannotHideOldTargetCollisionOrIdentityChanges()
    {
        var scope = Scope();
        var aliases = new[]
        {
            scope with { ExistingCredentialReference = $" google-repair.{RepairId:N} " },
            scope with { AccountId = " synthetic-account " },
            scope with { ProjectId = " synthetic-project " },
        };
        foreach (var alias in aliases)
            Assert.Throws<ArgumentException>(() => GoogleCredentialRepairProposalBuilder.Prepare(alias, alias, Absent, Digest('e'), RepairId));
    }

    [Fact]
    public void AccountIdentityUsesActualProviderAccountLengthLimit()
    {
        var valid = Scope() with { AccountId = new string('a', 64) };
        Assert.Equal(valid, GoogleCredentialRepairProposalBuilder.Prepare(valid, valid, Absent, Digest('e'), RepairId).Scope);
        var invalid = valid with { AccountId = new string('a', 65) };
        Assert.Throws<ArgumentException>(() => GoogleCredentialRepairProposalBuilder.Prepare(invalid, invalid, Absent, Digest('e'), RepairId));
    }
}
