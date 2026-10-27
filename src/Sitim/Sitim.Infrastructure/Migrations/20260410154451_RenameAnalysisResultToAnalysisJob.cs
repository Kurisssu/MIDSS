using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameAnalysisResultToAnalysisJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analysis_results");

            migrationBuilder.CreateTable(
                name: "ai_analysis_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    hangfire_job_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Queued"),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    performed_by_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PredictionClass = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    Probabilities = table.Column<string>(type: "text", nullable: true),
                    ProcessingTimeMs = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_analysis_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_analysis_jobs_ai_models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "ai_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_analysis_jobs_imaging_studies_StudyId",
                        column: x => x.StudyId,
                        principalTable: "imaging_studies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_jobs_hangfire_job_id",
                table: "ai_analysis_jobs",
                column: "hangfire_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_jobs_ModelId",
                table: "ai_analysis_jobs",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_jobs_PerformedByUserId",
                table: "ai_analysis_jobs",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_jobs_status_created_at_utc",
                table: "ai_analysis_jobs",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_jobs_StudyId",
                table: "ai_analysis_jobs",
                column: "StudyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analysis_jobs");

            migrationBuilder.CreateTable(
                name: "ai_analysis_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionClass = table.Column<int>(type: "integer", nullable: true),
                    Probabilities = table.Column<string>(type: "text", nullable: true),
                    ProcessingTimeMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_analysis_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_analysis_results_ai_models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "ai_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_analysis_results_imaging_studies_StudyId",
                        column: x => x.StudyId,
                        principalTable: "imaging_studies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_results_ModelId",
                table: "ai_analysis_results",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_results_PerformedAt",
                table: "ai_analysis_results",
                column: "PerformedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_results_StudyId",
                table: "ai_analysis_results",
                column: "StudyId");
        }
    }
}
