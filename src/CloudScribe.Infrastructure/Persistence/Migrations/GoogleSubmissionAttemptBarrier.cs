using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class GoogleSubmissionAttemptBarrier : Migration
{
    public const string MigrationId = "20261027010000_GoogleSubmissionAttemptBarrier";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("ErrorStatus", "google_generation_queue_states", type: "INTEGER", nullable: false, defaultValue: 0);
        // Historical rows do not prove no dispatch. Preserve every prior value/reservation
        // and fail closed; only future explicitly created pending rows start at zero.
        migrationBuilder.AddColumn<bool>("SubmissionAttempted", "google_generation_queue_states", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>("LocalAttemptId", "google_generation_queue_states", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("DiagnosticCode", "google_generation_queue_states", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("PayloadSha256", "google_generation_queue_states", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("PayloadBytes", "google_generation_queue_states", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<long>("OutcomeObservedAtUnixMilliseconds", "google_generation_queue_states", type: "INTEGER", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removing durable submission barriers requires a separately reviewed recovery plan; never reset attempted requests.");
}
