using System.Security.Cryptography;
using CloudScribe.Application.Pricing;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class EfPricingContractOverrideStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory,
    StrictJsonObjectReader strictJsonReader,
    TimeProvider timeProvider) : IPricingContractOverrideStore
{
    public async Task<PricingContractOverrideSnapshot> SaveInactiveAsync(
        ReadOnlyMemory<byte> utf8ContractOverride,
        string label,
        string provenanceId,
        CancellationToken cancellationToken = default)
    {
        if (utf8ContractOverride.IsEmpty)
        {
            throw new ArgumentException("Pricing contract override bytes cannot be empty.", nameof(utf8ContractOverride));
        }

        string normalizedLabel = NormalizeText(label, nameof(label), 240);
        string normalizedProvenance = NormalizeText(provenanceId, nameof(provenanceId), 160);
        using System.Text.Json.JsonDocument _ = strictJsonReader.Parse(utf8ContractOverride);

        byte[] bytes = utf8ContractOverride.ToArray();
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            PricingContractOverrideEntity? existing = await context.PricingContractOverrides
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Sha256 == sha256, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.OverrideBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException("Stored pricing override bytes do not match their SHA-256 identity.");
                }

                return Map(existing);
            }

            PricingContractOverrideEntity entity = new()
            {
                Id = Guid.NewGuid(),
                Sha256 = sha256,
                OverrideBytes = bytes,
                Label = normalizedLabel,
                ProvenanceId = normalizedProvenance,
                CapturedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            context.PricingContractOverrides.Add(entity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Map(entity);
        }
    }

    public async Task<IReadOnlyList<PricingContractOverrideSnapshot>> ListInactiveAsync(
        CancellationToken cancellationToken = default)
    {
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<PricingContractOverrideEntity> entities = await context.PricingContractOverrides
                .AsNoTracking()
                .OrderByDescending(item => item.CapturedAtUnixMilliseconds)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(Map).ToArray();
        }
    }

    private static PricingContractOverrideSnapshot Map(PricingContractOverrideEntity entity) => new(
        entity.Id,
        entity.Sha256,
        entity.OverrideBytes.LongLength,
        entity.Label,
        entity.ProvenanceId,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CapturedAtUnixMilliseconds));

    private static string NormalizeText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength || ContainsUnsafeText(normalized))
        {
            throw new ArgumentException($"Value must be 1-{maximumLength} visible characters.", parameterName);
        }

        return normalized;
    }

    private static bool ContainsUnsafeText(string value) => value.Any(static character =>
        char.IsControl(character)
        || char.IsSurrogate(character)
        || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
}
