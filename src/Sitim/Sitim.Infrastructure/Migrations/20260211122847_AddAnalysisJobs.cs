using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op. This migration originally altered the legacy "analysis_jobs"
            // table, but the migration that created that table was never committed,
            // so the operations cannot replay on a fresh database. The legacy table
            // is dropped later (RemoveLegacyAnalysisJobsTable) and is not part of the
            // final schema, so neutralizing this migration is schema-neutral.
            // See also: AddInstitutionMultiTenancy, RemoveLegacyAnalysisJobsTable.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op. See Up().
        }
    }
}
