from pathlib import Path

ROOT = Path.cwd()


def replace_once(relative_path: str, old: str, new: str) -> None:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{relative_path}: expected exactly one repair target, found {count}")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


stage3 = "tests/CloudScribe.Infrastructure.Tests/Stage3MigrationTests.cs"
recovery = "tests/CloudScribe.Infrastructure.Tests/DatabaseRecoveryTests.cs"

replace_once(
    stage3,
    "public async Task FreshDatabaseAppliesExecutableStage2AndStage3Migrations()",
    "public async Task FreshDatabaseAppliesExecutableStage2ThroughStage4Migrations()",
)

replace_once(
    stage3,
    """            Assert.Equal(
                [Stage2Baseline.MigrationId, Stage3Documents.MigrationId, Stage3DocumentWorkflow.MigrationId],
                migrations);""",
    """            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                ],
                migrations);""",
)

replace_once(
    stage3,
    """            Assert.Equal(3, (await upgraded.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());""",
    """            string[] migrations = (await upgraded.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                ],
                migrations);""",
)

replace_once(
    stage3,
    """            Assert.Equal(3, (await upgrade.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());""",
    """            string[] migrations = (await upgrade.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                ],
                migrations);""",
)

replace_once(
    recovery,
    """            Assert.Equal(3, (await current.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)).Count());""",
    """            string[] currentMigrations = (await current.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                ],
                currentMigrations);""",
)

print("PASS: strengthened four Stage-3/current migration assertions for the exact Stage-4 migration chain.")
