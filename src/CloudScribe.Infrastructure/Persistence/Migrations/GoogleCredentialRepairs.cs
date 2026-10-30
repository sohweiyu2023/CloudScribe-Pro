using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class GoogleCredentialRepairs : Migration
{
    public const string MigrationId = "20261029140000_GoogleCredentialRepairs";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE google_credential_repairs (
            RepairId TEXT NOT NULL PRIMARY KEY, AccountId TEXT NOT NULL, ExpectedAccountJson TEXT NOT NULL,
            FinancialSha256 TEXT NOT NULL, IdentityJson TEXT NOT NULL, StagedReference TEXT NOT NULL UNIQUE,
            Phase TEXT NOT NULL CHECK(Phase IN('Prepared','Staging','Staged','Committed','Abandoned')),
            ApprovalSha256 TEXT NOT NULL, ExpiresAt INTEGER NOT NULL, CreatedAt INTEGER NOT NULL);
        CREATE UNIQUE INDEX credential_repair_one_active ON google_credential_repairs(AccountId)
            WHERE Phase <> 'Committed' AND Phase <> 'Abandoned';
        CREATE TABLE google_credential_repair_events (
            Sequence INTEGER PRIMARY KEY AUTOINCREMENT, RepairId TEXT NOT NULL, Phase TEXT NOT NULL,
            ApprovalSha256 TEXT NOT NULL, ApprovalJson TEXT NOT NULL, ObservedAt INTEGER NOT NULL,
            FOREIGN KEY(RepairId) REFERENCES google_credential_repairs(RepairId) ON DELETE RESTRICT);
        CREATE TRIGGER credential_repair_event_no_update BEFORE UPDATE ON google_credential_repair_events
            BEGIN SELECT RAISE(ABORT,'Credential repair lineage is immutable.'); END;
        CREATE TRIGGER credential_repair_event_no_delete BEFORE DELETE ON google_credential_repair_events
            BEGIN SELECT RAISE(ABORT,'Credential repair lineage is retained.'); END;
        CREATE TRIGGER credential_repair_committed_no_update BEFORE UPDATE ON google_credential_repairs
            WHEN OLD.Phase = 'Committed' BEGIN SELECT RAISE(ABORT,'Committed credential binding is retained.'); END;
        CREATE TRIGGER credential_repair_no_delete BEFORE DELETE ON google_credential_repairs
            BEGIN SELECT RAISE(ABORT,'Credential repair history is retained.'); END;
        CREATE TRIGGER credential_repair_binding_immutable BEFORE UPDATE ON google_credential_repairs
            WHEN NEW.RepairId<>OLD.RepairId OR NEW.AccountId<>OLD.AccountId OR NEW.ExpectedAccountJson<>OLD.ExpectedAccountJson
              OR NEW.FinancialSha256<>OLD.FinancialSha256 OR NEW.IdentityJson<>OLD.IdentityJson
              OR NEW.StagedReference<>OLD.StagedReference OR NEW.CreatedAt<>OLD.CreatedAt
            BEGIN SELECT RAISE(ABORT,'Credential repair binding is immutable.'); END;
        CREATE TRIGGER credential_repair_queue_insert BEFORE INSERT ON google_generation_queue_states
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=NEW.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair requires generation to remain stopped.'); END;
        CREATE TRIGGER credential_repair_queue_update BEFORE UPDATE ON google_generation_queue_states
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=OLD.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair requires generation to remain stopped.'); END;
        CREATE TRIGGER credential_repair_spend_insert BEFORE INSERT ON google_generation_spend_authorizations
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=NEW.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair cannot authorize spending.'); END;
        CREATE TRIGGER credential_repair_spend_delete BEFORE DELETE ON google_generation_spend_authorizations
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=OLD.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair retains existing reservations.'); END;
        CREATE TRIGGER credential_repair_recovery_insert BEFORE INSERT ON google_recovery_attempts
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=NEW.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair cannot authorize recovery spending.'); END;
        CREATE TRIGGER credential_repair_article_insert BEFORE INSERT ON chirp_article_attempts
            WHEN EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=NEW.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair cannot authorize article spending.'); END;
        CREATE TRIGGER credential_repair_audition_insert BEFORE INSERT ON voice_lab_audition_authorizations
            WHEN NEW.ProviderId='google-cloud-text-to-speech' AND EXISTS(SELECT 1 FROM google_credential_repairs WHERE AccountId=NEW.AccountId AND Phase NOT IN ('Committed','Abandoned'))
            BEGIN SELECT RAISE(ABORT,'Credential repair cannot authorize auditions.'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Credential binding history cannot be discarded by downgrade.");
}
