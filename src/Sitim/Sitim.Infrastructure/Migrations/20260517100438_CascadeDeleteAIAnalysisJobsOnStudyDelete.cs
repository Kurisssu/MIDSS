using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CascadeDeleteAIAnalysisJobsOnStudyDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_analysis_jobs_imaging_studies_StudyId",
                table: "ai_analysis_jobs");

            migrationBuilder.AddForeignKey(
                name: "FK_ai_analysis_jobs_imaging_studies_StudyId",
                table: "ai_analysis_jobs",
                column: "StudyId",
                principalTable: "imaging_studies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_analysis_jobs_imaging_studies_StudyId",
                table: "ai_analysis_jobs");

            migrationBuilder.AddForeignKey(
                name: "FK_ai_analysis_jobs_imaging_studies_StudyId",
                table: "ai_analysis_jobs",
                column: "StudyId",
                principalTable: "imaging_studies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
