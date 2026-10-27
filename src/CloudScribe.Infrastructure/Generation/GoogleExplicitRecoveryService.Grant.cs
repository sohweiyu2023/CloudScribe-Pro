using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    // The independently admitted digest is the immutable grant identity. Every reservation counts,
    // including pending, consumed, failed and uncertain attempts. Nothing releases this allowance.
    private static async Task RequireGrantCapacityAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryAssociationProof proof, string account, string project, long maximum, bool creating,
        CancellationToken cancellationToken)
    {
        if (proof.MaximumNewAttempts is < 1 or > 200 || proof.MaximumAdditionalReservationMinorUnits < 0 || maximum < 0)
            throw new InvalidOperationException("An independently admitted bounded recovery grant is required.");
        using var command = Command(connection, transaction, """
            SELECT AccountId,ProjectId,OperationId,LogicalItemId,RootHistoricalKey,RootHistoricalStateSha256,
            GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits,ReservedMinorUnits
            FROM google_recovery_attempts WHERE AdmissionSha256=$grant;
            """, ("$grant", proof.AdmissionSha256));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int attempts = 0; long reserved = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(reader.GetString(0), account, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), project, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), proof.LogicalNarrationId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(4), proof.RootHistoricalKey, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(5), proof.RootHistoricalStateSha256, StringComparison.Ordinal)
                || reader.IsDBNull(6) || reader.GetInt32(6) != proof.MaximumNewAttempts
                || reader.IsDBNull(7) || reader.GetInt64(7) != proof.MaximumAdditionalReservationMinorUnits)
                throw new InvalidOperationException("Recovery grant identity or immutable limits changed; retain its history.");
            long amount = reader.GetInt64(8);
            if (amount < 0) throw new InvalidOperationException("Recovery grant history contains an invalid retained reservation.");
            attempts = checked(attempts + 1); reserved = checked(reserved + amount);
        }
        if ((!creating && attempts == 0) || checked(attempts + (creating ? 1 : 0)) > proof.MaximumNewAttempts
            || checked(reserved + (creating ? maximum : 0)) > proof.MaximumAdditionalReservationMinorUnits)
            throw new InvalidOperationException("The independently admitted recovery grant is exhausted.");
    }
}
