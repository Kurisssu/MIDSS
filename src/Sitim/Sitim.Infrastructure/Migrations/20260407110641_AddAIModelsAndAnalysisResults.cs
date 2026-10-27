using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAIModelsAndAnalysisResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Check if index exists before dropping to avoid errors
            migrationBuilder.Sql(@"
                DO $$ 
                BEGIN 
                    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'IX_institutions_OrthancLabel') THEN
                        DROP INDEX ""IX_institutions_OrthancLabel"";
                    END IF;
                END $$;
            ");

            // Check if column exists before dropping
            migrationBuilder.Sql(@"
                DO $$ 
                BEGIN 
                    IF EXISTS (SELECT 1 FROM information_schema.columns 
                               WHERE table_name = 'institutions' AND column_name = 'OrthancLabel') THEN
                        ALTER TABLE institutions DROP COLUMN ""OrthancLabel"";
                    END IF;
                END $$;
            ");

            // Add OrthancBaseUrl column if it doesn't exist
            migrationBuilder.Sql(@"
                DO $$ 
                BEGIN 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                                   WHERE table_name = 'institutions' AND column_name = 'OrthancBaseUrl') THEN
                        ALTER TABLE institutions ADD COLUMN ""OrthancBaseUrl"" character varying(256) NOT NULL DEFAULT '';
                    END IF;
                END $$;
            ");

            migrationBuilder.CreateTable(
                name: "ai_models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Task = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StorageFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Accuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NumClasses = table.Column<int>(type: "integer", nullable: true),
                    InputShape = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TrainingSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_analysis_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionClass = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    Probabilities = table.Column<string>(type: "text", nullable: true),
                    ProcessingTimeMs = table.Column<int>(type: "integer", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_ai_models_Task_IsActive",
                table: "ai_models",
                columns: new[] { "Task", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analysis_results");

            migrationBuilder.DropTable(
                name: "ai_models");

            migrationBuilder.DropColumn(
                name: "OrthancBaseUrl",
                table: "institutions");

            migrationBuilder.AddColumn<string>(
                name: "OrthancLabel",
                table: "institutions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_institutions_OrthancLabel",
                table: "institutions",
                column: "OrthancLabel",
                unique: true);
        }
    }
}
