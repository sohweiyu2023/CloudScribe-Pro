using System.Security.Cryptography;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Full trusted compile context digest; neither this digest nor caller input grants authority.</summary>
public static class ChirpResumeRuntimeBinding
{
    public static string Hash(GoogleGenerationUiExecutionSnapshot snapshot, string currency, int scale, long estimate) =>
        ChirpPolicyValidation.Hash(new
        {
            Kind = "reserved-catalog-runtime-v1",
            snapshot.UiSelection,
            snapshot.AdmittedTrust,
            snapshot.ProviderRequest.ProviderStableId,
            snapshot.ProviderRequest.OperationStableId,
            snapshot.ProviderRequest.AccountId,
            snapshot.ProviderRequest.OutputFormat,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot.ProviderRequest.CompiledPayload.Span)),
            PayloadBytes = snapshot.ProviderRequest.CompiledPayload.Length,
            currency,
            scale,
            estimate,
        });
}
