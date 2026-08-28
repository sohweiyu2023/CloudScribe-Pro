namespace CloudScribe.Application.Pricing;

public interface IPricingContractOverrideStore
{
    Task<PricingContractOverrideSnapshot> SaveInactiveAsync(
        ReadOnlyMemory<byte> utf8ContractOverride,
        string label,
        string provenanceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PricingContractOverrideSnapshot>> ListInactiveAsync(
        CancellationToken cancellationToken = default);
}
