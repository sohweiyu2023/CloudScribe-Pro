using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudScribeDbContext))]
[Migration(MigrationId)]
public sealed class ChirpWholeArticleJobs : Migration
{
    public const string MigrationId = "20261102090000_ChirpWholeArticleJobs";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chirp_whole_article_jobs (
            JobId TEXT NOT NULL PRIMARY KEY, PolicySha256 TEXT NOT NULL, PlanSha256 TEXT NOT NULL,
            JobJson TEXT NOT NULL, JobSha256 TEXT NOT NULL, UNIQUE(PolicySha256,PlanSha256));
        CREATE TABLE chirp_whole_article_events (
            JobId TEXT NOT NULL, Sequence INTEGER NOT NULL CHECK(Sequence>=1),
            JobJson TEXT NOT NULL, JobSha256 TEXT NOT NULL,
            PRIMARY KEY(JobId,Sequence), FOREIGN KEY(JobId) REFERENCES chirp_whole_article_jobs(JobId) ON DELETE RESTRICT);
        CREATE TRIGGER chirp_whole_article_events_no_update BEFORE UPDATE ON chirp_whole_article_events
            BEGIN SELECT RAISE(ABORT,'Article consent and transition history is append-only'); END;
        CREATE TRIGGER chirp_whole_article_events_no_delete BEFORE DELETE ON chirp_whole_article_events
            BEGIN SELECT RAISE(ABORT,'Article consent and transition history must be retained'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Article consent and stopped-job evidence must be retained.");
}
