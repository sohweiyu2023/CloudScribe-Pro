namespace CloudScribe.Infrastructure.Generation;

public static class ChirpAdditionalAdmissionValidation
{
    public static void RequireValid(ChirpArticleAdmissionCatalog catalog, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Families is null || catalog.Families.Any(x => x is null)
            || catalog.Families.Select(x => x.FamilyId).Distinct().Count() != catalog.Families.Count
            || catalog.Families.Select(x => x.Policy.SourceApprovalSha256).Distinct(StringComparer.Ordinal).Count() != catalog.Families.Count)
            throw new InvalidDataException("Additional article families are absent or ambiguous.");
        var policies = catalog.Families.Select(x => x.Policy).ToList();
        if (catalog.LegacyPolicy is { } legacy) policies.Add(legacy);
        if (policies.Any(x => x is null) || policies.Select(x => x.SourceApprovalSha256).Distinct(StringComparer.Ordinal).Count() != policies.Count
            || policies.Select(ChirpPolicyValidation.Hash).Distinct(StringComparer.Ordinal).Count() != policies.Count
            || policies.SelectMany(x => x.Articles).Select(x => x.Proposal.DocumentId).Distinct().Count() != policies.Sum(x => x.Articles.Count)
            || policies.SelectMany(x => x.Articles).Select(x => x.Proposal.PlanSha256).Distinct(StringComparer.Ordinal).Count() != policies.Sum(x => x.Articles.Count))
            throw new InvalidDataException("An article, plan or policy cannot belong to multiple admission families.");
        // Expiry stops fresh authority, not accounting for an already charged independent family.
        foreach (var family in catalog.Families) RequireValid(family, family.Policy.ExpiresAtUtc <= now ? family.Policy.ExpiresAtUtc.AddTicks(-1) : now);
    }

    public static void RequireValid(ChirpAdditionalAdmissionFamily family, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(family);
        var p = family.Policy;
        ChirpPolicyValidation.RequireValid(p, now);
        var b = p.Budget;
        if (family.FamilyId == Guid.Empty || family.AllInCeilingMinorUnits < 1
            || family.ProvisionMinorUnits < 0 || family.ProvisionMinorUnits >= family.AllInCeilingMinorUnits
            || !ChirpPolicyValidation.IsHash(family.ReviewedBaselineSha256)
            || !string.Equals(family.ReviewedBaselineSha256, b.ReviewedEvidenceSha256, StringComparison.Ordinal)
            || b.ProjectCommittedMinorUnits != family.ProvisionMinorUnits || b.AllocationCommittedMinorUnits != family.ProvisionMinorUnits
            || b.ProjectCeilingMinorUnits != family.AllInCeilingMinorUnits || b.AllocationCeilingMinorUnits != family.AllInCeilingMinorUnits
            || p.MaximumAdditionalReservationMinorUnits > family.AllInCeilingMinorUnits - family.ProvisionMinorUnits
            || p.SchemaVersion != 3 || p.WholeArticleGeneration is null || p.SuccessorGrant is not null || p.DiagnosticEvidence is not null
            || p.SentenceRecovery is not null || p.RecoveryContinuation is not null || p.RecoveryLimitRevision is not null || p.CompletionContinuity is not null
            || p.LegacyAssociations is not null || p.LegacyAssociationRows is not null
            || b.QueueRows.Count != 0 || b.SpendRows.Count != 0 || b.RecoveryRows.Count != 0 || b.TransitionRows!.Count != 0
            || p.ScopeRows.Count != 0 || p.PriorArticleAttemptRows.Count != 0 || p.PriorSuccessorAttemptRows!.Count != 0)
            throw new InvalidDataException("Additional admission must preserve an independent all-in allowance and authorize fresh roots only.");
        foreach (var a in p.Articles)
        {
            ChirpArticlePlanCompiler.RequireIntegrity(a.Proposal);
            if (a.Proposal.Refinement is not null || a.Proposal.SentenceSuccessor is not null || a.ReusedAudio.Count != 0
                || a.Proposal.Parts.Any(x => x.Range.ReusedReceiptSha256 is not null)
                || a.Lineage.History.Count != 1 || a.Lineage.History.Any(x => x.Attempts.Count != 0 || x.DocumentId != a.Proposal.DocumentId
                    || x.RevisionId != a.Proposal.RevisionId || x.StartScalar != 0 || x.EndScalarExclusive != a.Proposal.SourceScalars
                    || !string.Equals(x.TextSha256, a.Proposal.SourceSha256, StringComparison.Ordinal))
                || a.AllowedSegments.Count != a.Proposal.Parts.Count)
                throw new InvalidDataException("Additional plans cannot inherit historical attempts, accepted coverage or successor authority.");
        }
        if (family.Requests is null || family.Requests.Count != p.Articles.Sum(x => x.AllowedSegments.Count)
            || family.Requests.Select(x => (x.PlanSha256, x.CoverageOrdinal)).Distinct().Count() != family.Requests.Count)
            throw new InvalidDataException("Every additional request needs one exact reviewed envelope.");
        foreach (var pin in family.Requests)
        {
            var a = p.Articles.SingleOrDefault(x => string.Equals(x.Proposal.PlanSha256, pin.PlanSha256, StringComparison.Ordinal));
            var part = a?.Proposal.Parts.SingleOrDefault(x => x.CoverageOrdinal == pin.CoverageOrdinal);
            var limit = a?.AllowedSegments.SingleOrDefault(x => x.CoverageOrdinal == pin.CoverageOrdinal);
            var e = pin.Envelope;
            if (part is null || limit is null || e is null || pin.EstimateMinorUnits < 0 || pin.EstimateMinorUnits > limit.MaximumMinorUnits
                || e.RecoveryAuthorizationId is not null || e.ChirpAuthorizationId is not null || e.RequestRevision < 0
                || string.IsNullOrWhiteSpace(e.CredentialReferenceId) || string.IsNullOrWhiteSpace(e.CapabilityProvenanceId)
                || string.IsNullOrWhiteSpace(e.PricingProvenanceId) || !string.Equals(e.AccountId, p.AccountId, StringComparison.Ordinal)
                || !string.Equals(e.VoiceName, a!.Proposal.VoiceName, StringComparison.Ordinal) || !string.Equals(e.AudioEncoding, "MP3", StringComparison.Ordinal)
                || !string.Equals(e.CompiledPayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || e.CompiledPayloadBytes != part.PayloadBytes)
                throw new InvalidDataException("An additional request differs from its exact reviewed provider evidence.");
        }
    }
}
