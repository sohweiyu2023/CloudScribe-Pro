using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class ChirpArticlePreparationService
{
    /// <summary>Supported pre-admission review: current trusted compile, no queue publication, reserve, capture or dispatch.</summary>
    public async Task<ChirpReservedResumeDraft> CreateReservedResumeDraftAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        int ordinal, ChirpArticleAdmissionPolicy historicalPolicy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(historicalPolicy);
        historicalPolicy = System.Text.Json.JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(historicalPolicy))!;
        ChirpResumeSelectionBinding.RequireFresh(selection, clock.GetUtcNow());
        var inspected = await articles.InspectReservedResumeSourceAsync(selection.DocumentId, ordinal, historicalPolicy, token).ConfigureAwait(false);
        RequireSourceSelection(inspected.Source, selection);
        if (!string.Equals(selection.AccountStableId, historicalPolicy.AccountId, StringComparison.Ordinal)
            || !string.Equals(selection.ProjectStableId, historicalPolicy.ProjectId, StringComparison.Ordinal)
            || !string.Equals(selection.VoiceStableId, inspected.Source.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(selection.LanguageCode, inspected.Source.Proposal.LanguageCode, StringComparison.Ordinal)) throw new InvalidOperationException("The exact retained request selection changed.");
        var current = await compilation.CompileReadonlyAsync(selection with { ExactText = inspected.Source.ExactText }, token).ConfigureAwait(false);
        Guid catalog = Guid.Parse(selection.CapabilityEvidenceId);
        var expires = await articles.InspectResumeCatalogExpiryAsync(selection.AccountStableId, catalog, token).ConfigureAwait(false);
        var retained = await articles.InspectReservedResumeAdmissionAsync(inspected.AuthorizationId, historicalPolicy, catalog, expires, token).ConfigureAwait(false);
        var candidate = current.Envelope with { ChirpAuthorizationId = inspected.AuthorizationId };
        if ((candidate with { RequestRevision = retained.OriginalEnvelope.RequestRevision }) != retained.OriginalEnvelope)
            throw new InvalidOperationException("Catalog review cannot change the retained payload, account, credential, price or voice.");
        string? predecessor = await articles.InspectResumeMappingHeadAsync(inspected.AuthorizationId, token).ConfigureAwait(false);
        if (retained != await articles.InspectReservedResumeAdmissionAsync(inspected.AuthorizationId, historicalPolicy, catalog, expires, token).ConfigureAwait(false))
            throw new InvalidOperationException("Retained request evidence changed during private review.");
        return new(historicalPolicy, retained, candidate, catalog, expires, ChirpResumeSelectionBinding.Hash(selection), ResumeRuntimeHash(current), ResumeRequestIdentity(current), predecessor);
    }
}
