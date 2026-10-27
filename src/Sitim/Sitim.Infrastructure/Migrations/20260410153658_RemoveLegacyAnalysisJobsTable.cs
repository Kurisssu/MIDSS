using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyAnalysisJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op. The legacy "analysis_jobs" table is never created on a fresh
            // database (its create migration was never committed), so there is
            // nothing to drop. See AddAnalysisJobs / AddInstitutionMultiTenancy.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op. See Up().
        }
    }
}
