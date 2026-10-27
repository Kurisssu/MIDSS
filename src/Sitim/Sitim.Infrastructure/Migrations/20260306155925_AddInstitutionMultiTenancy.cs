using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInstitutionMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "institution_id",
                table: "patients",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "institution_id",
                table: "imaging_studies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstitutionId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            // NOTE: columns/index for the legacy "analysis_jobs" table removed here.
            // That table's create migration was never committed, so it does not exist
            // on a fresh database. It is dropped later and is not part of the final
            // schema. See AddAnalysisJobs / RemoveLegacyAnalysisJobsTable.

            migrationBuilder.CreateTable(
                name: "institutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrthancLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_institutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_patients_institution_id",
                table: "patients",
                column: "institution_id");

            migrationBuilder.CreateIndex(
                name: "IX_imaging_studies_institution_id",
                table: "imaging_studies",
                column: "institution_id");

            migrationBuilder.CreateIndex(
                name: "IX_institutions_OrthancLabel",
                table: "institutions",
                column: "OrthancLabel",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_institutions_Slug",
                table: "institutions",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "institutions");

            migrationBuilder.DropIndex(
                name: "IX_patients_institution_id",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "IX_imaging_studies_institution_id",
                table: "imaging_studies");

            migrationBuilder.DropColumn(
                name: "institution_id",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "institution_id",
                table: "imaging_studies");

            migrationBuilder.DropColumn(
                name: "InstitutionId",
                table: "AspNetUsers");
        }
    }
}
