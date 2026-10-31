using CloudScribe.Application.Logging;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    // Exact database state, read in the same transaction as the final dispatch barrier. No secret values are read.
    private async Task<string> ResumeProviderEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleGenerationSubmissionEnvelope envelope, string project, Guid catalogId, DateTimeOffset catalogExpiry, CancellationToken token)
    {
        string account = envelope.AccountId;
        var args = new (string, object?)[] { ("$provider", GoogleGenerationProvider.StableProviderId), ("$account", account),
            ("$project", project), ("$voice", envelope.VoiceName), ("$catalog", catalogId.ToString("D")) };
        var accounts = await ResumeRowsAsync(connection, transaction,
            "SELECT CredentialTargetName,EndpointId,RegionId,EndpointOrigin,IsEnabled,Revision,UpdatedAtUnixMilliseconds FROM provider_accounts WHERE ProviderStableId=$provider AND AccountId=$account;", args, token).ConfigureAwait(false);
        var catalogs = await ResumeRowsAsync(connection, transaction,
            "SELECT Id,CredentialTargetName,EndpointId,RegionId,EndpointOrigin,CapturedAtUnixMilliseconds,ExpiresAtUnixMilliseconds,ProvenanceId FROM provider_capability_snapshots WHERE ProviderStableId=$provider AND AccountId=$account ORDER BY CapturedAtUnixMilliseconds DESC,Id DESC LIMIT 1;", args, token).ConfigureAwait(false);
        var entries = await ResumeRowsAsync(connection, transaction,
            "SELECT CapabilityId,State,LifecycleState,DisabledReason FROM provider_capability_entries WHERE lower(SnapshotId)=lower($catalog) ORDER BY CapabilityId;", args, token).ConfigureAwait(false);
        var projects = await ResumeRowsAsync(connection, transaction,
            "SELECT CredentialReferenceId,CapabilityProvenanceId,EndpointId,RegionId,EndpointOrigin,Authorized,CapturedAtUnixMilliseconds,ExpiresAtUnixMilliseconds FROM google_generation_project_authorizations WHERE AccountId=$account AND ProjectId=$project AND ModelId=$voice;", args, token).ConfigureAwait(false);
        if (accounts.Count != 1 || catalogs.Count != 1 || projects.Count != 1) throw ResumeRejected(LocalRequestReason.ResumeProviderChanged);
        var a = accounts[0]; var c = catalogs[0]; var p = projects[0];
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!Equals(a[4], 1L) || !Equals(a[0], envelope.CredentialReferenceId)
            || !Guid.TryParse(c[0] as string, out var id) || id != catalogId
            || !Equals(c[1], a[0]) || !Equals(c[2], a[1]) || !Equals(c[3], a[2]) || !SameResumeOrigin(c[4], a[3])
            || (long)a[6]! > (long)c[5]! || (long)c[5]! > now || (long)c[6]! <= now
            || (long)c[6]! != catalogExpiry.ToUnixTimeMilliseconds()
            || !Equals(c[7], envelope.CapabilityProvenanceId)
            || !Equals(p[0], a[0]) || !Equals(p[1], c[7]) || !Equals(p[2], a[1]) || !Equals(p[3], a[2]) || !SameResumeOrigin(p[4], a[3])
            || !Equals(p[5], 1L) || (long)p[6]! > now || (long)p[7]! <= now
            || entries.Count(x => Equals(x[0], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[1], 1L) && !Equals(x[2], 4L) && x[3] is null) != 1)
            throw ResumeRejected(LocalRequestReason.ResumeProviderChanged);
        var prices = await ResumeRowsAsync(connection, transaction, "SELECT * FROM pricing_catalog_snapshots ORDER BY rowid;", [], token).ConfigureAwait(false);
        var activations = await ResumeRowsAsync(connection, transaction, "SELECT * FROM pricing_catalog_activations ORDER BY Sequence;", [], token).ConfigureAwait(false);
        var overrides = await ResumeRowsAsync(connection, transaction, "SELECT * FROM pricing_contract_overrides ORDER BY rowid;", [], token).ConfigureAwait(false);
        var activePrices = await ResumeRowsAsync(connection, transaction,
            "SELECT s.Sha256,s.CatalogBytes,s.TrustState FROM pricing_catalog_activations a JOIN pricing_catalog_snapshots s ON a.SnapshotId=s.Id ORDER BY a.Sequence DESC LIMIT 1;", [], token).ConfigureAwait(false);
        if (activePrices.Count != 1 || !Equals(activePrices[0][0], envelope.PricingProvenanceId)
            || activePrices[0][1] is not byte[] priceBytes || !string.Equals(ChirpPolicyFiles.Hash(priceBytes), envelope.PricingProvenanceId, StringComparison.Ordinal)
            || activePrices[0][2] is not (2L or 4L)) throw ResumeRejected(LocalRequestReason.ResumeProviderChanged);
        return ChirpPolicyValidation.Hash(new { accounts, catalogs, entries, projects, prices, activations, overrides });
    }

    // ProviderAccountReference persists Uri.AbsoluteUri (with root slash), while
    // production project authorization persists GetLeftPart(Authority) (without it).
    // Compare only validated HTTPS origins; exact endpoint IDs still bind API paths.
    private static bool SameResumeOrigin(object? left, object? right)
    {
        static bool Parse(object? value, out Uri? origin)
        {
            origin = null;
            return value is string text && Uri.TryCreate(text, UriKind.Absolute, out origin)
                && string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && origin.IsDefaultPort && string.IsNullOrEmpty(origin.UserInfo)
                && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment)
                && string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal);
        }

        return Parse(left, out var first) && Parse(right, out var second)
            && Uri.Compare(first, second, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static async Task<IReadOnlyList<object?[]>> ResumeRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, (string, object?)[] args, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, sql, args);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<object?[]> rows = []; long size = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (rows.Count >= 10_000) throw new InvalidDataException("Reserved evidence exceeds its read bound.");
            var values = new object[reader.FieldCount]; reader.GetValues(values);
            var row = values.Select(x => x is DBNull ? null : x).ToArray();
            size += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(row).Length;
            if (size > 16 * 1024 * 1024) throw new InvalidDataException("Reserved evidence exceeds its byte bound.");
            rows.Add(row);
        }
        return rows;
    }
}
