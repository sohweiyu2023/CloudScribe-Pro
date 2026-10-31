using CloudScribe.Application.Logging;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task RequireStoredResumeMappingAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpReservationResumePolicy policy, bool required, CancellationToken token)
    {
        var chain = await ReadResumeChainAsync(connection, transaction, policy.AuthorizationId, token).ConfigureAwait(false);
        var head = chain.Count == 0 ? null : chain[^1];
        string sha = ChirpPolicyValidation.Hash(policy);
        if (head is not null && string.Equals(head.Sha256, sha, StringComparison.Ordinal)) return;
        if (required || chain.Any(x => string.Equals(x.Sha256, sha, StringComparison.Ordinal)
            || x.Policy.MappingId == policy.MappingId || string.Equals(x.Policy.SourceApprovalSha256, policy.SourceApprovalSha256, StringComparison.Ordinal))) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
        if (!string.Equals(policy.PriorMappingSha256, head?.Sha256, StringComparison.Ordinal)) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
        if (head is not null) RequireSameRetainedReservation(head.Policy, policy);
    }

    private static async Task<IReadOnlyList<StoredResumeMapping>> ReadResumeChainAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid authorization, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT MappingId,PolicyJson,PolicySha256,CreatedAtUnixMilliseconds,PriorMappingSha256,length(PolicyJson) FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=$id LIMIT 65;", ("$id", authorization.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<StoredResumeMapping> nodes = []; long bytes = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            long length = reader.GetInt64(5); bytes += length;
            if (nodes.Count >= 64 || length is < 1 or > 8 * 1024 * 1024 || bytes > 16 * 1024 * 1024) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
            string json = reader.GetString(1), sha = reader.GetString(2);
            var policy = ChirpPolicyFiles.Parse<ChirpReservationResumePolicy>(Encoding.UTF8.GetBytes(json));
            var created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
            ChirpReservationResumeValidation.RequireValid(policy, created);
            if (policy.AuthorizationId != authorization || !string.Equals(reader.GetString(0), policy.MappingId.ToString("N"), StringComparison.Ordinal)
                || !string.Equals(sha, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)
                || !string.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), policy.PriorMappingSha256, StringComparison.Ordinal)
                || !string.Equals(json, JsonSerializer.Serialize(policy), StringComparison.Ordinal)) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
            nodes.Add(new(policy, sha, created));
        }
        return OrderResumeChain(nodes);
    }

    private static List<StoredResumeMapping> OrderResumeChain(IReadOnlyList<StoredResumeMapping> nodes)
    {
        if (nodes.Count == 0) return [];
        if (nodes.Count(x => x.Policy.PriorMappingSha256 is null) != 1
            || nodes.Select(x => x.Sha256).Distinct(StringComparer.Ordinal).Count() != nodes.Count
            || nodes.Select(x => x.Policy.SourceApprovalSha256).Distinct(StringComparer.Ordinal).Count() != nodes.Count) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
        List<StoredResumeMapping> chain = [nodes.Single(x => x.Policy.PriorMappingSha256 is null)];
        while (true)
        {
            var next = nodes.Where(x => string.Equals(x.Policy.PriorMappingSha256, chain[^1].Sha256, StringComparison.Ordinal)).ToArray();
            if (next.Length == 0) break;
            if (next.Length != 1 || chain.Contains(next[0]) || next[0].CreatedAt < chain[^1].CreatedAt) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
            RequireSameRetainedReservation(chain[^1].Policy, next[0].Policy); chain.Add(next[0]);
        }
        if (chain.Count != nodes.Count) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
        return chain;
    }

    private static void RequireSameRetainedReservation(ChirpReservationResumePolicy previous, ChirpReservationResumePolicy current)
    {
        if (previous.OriginalEnvelope != current.OriginalEnvelope || previous.OriginalApprovedAtUtc != current.OriginalApprovedAtUtc
            || !string.Equals(previous.OriginalAttemptRowSha256, current.OriginalAttemptRowSha256, StringComparison.Ordinal)
            || !string.Equals(previous.CleanQueueRowSha256, current.CleanQueueRowSha256, StringComparison.Ordinal)
            || !string.Equals(previous.FinancialSha256, current.FinancialSha256, StringComparison.Ordinal)
            || !string.Equals(previous.ProfileRootSha256, current.ProfileRootSha256, StringComparison.Ordinal)
            || !string.Equals(ChirpPolicyValidation.Hash(previous.HistoricalPolicy), ChirpPolicyValidation.Hash(current.HistoricalPolicy), StringComparison.Ordinal)) throw ResumeRejected(LocalRequestReason.ResumeMappingChanged);
    }

    private sealed record StoredResumeMapping(ChirpReservationResumePolicy Policy, string Sha256, DateTimeOffset CreatedAt);
}
