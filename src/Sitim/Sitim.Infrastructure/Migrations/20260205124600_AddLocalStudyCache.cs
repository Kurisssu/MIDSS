using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalStudyCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PatientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PatientName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "imaging_studies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    orthanc_study_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    study_instance_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    study_date = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    modalities_in_study = table.Column<string[]>(type: "text[]", nullable: false),
                    PatientDbId = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imaging_studies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_imaging_studies_patients_PatientDbId",
                        column: x => x.PatientDbId,
                        principalTable: "patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "imaging_series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudyDbId = table.Column<Guid>(type: "uuid", nullable: false),
                    orthanc_series_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imaging_series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_imaging_series_imaging_studies_StudyDbId",
                        column: x => x.StudyDbId,
                        principalTable: "imaging_studies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_imaging_series_StudyDbId_orthanc_series_id",
                table: "imaging_series",
                columns: new[] { "StudyDbId", "orthanc_series_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imaging_studies_orthanc_study_id",
                table: "imaging_studies",
                column: "orthanc_study_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imaging_studies_PatientDbId",
                table: "imaging_studies",
                column: "PatientDbId");

            migrationBuilder.CreateIndex(
                name: "IX_imaging_studies_study_instance_uid",
                table: "imaging_studies",
                column: "study_instance_uid");

            migrationBuilder.CreateIndex(
                name: "IX_patients_PatientId",
                table: "patients",
                column: "PatientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "imaging_series");

            migrationBuilder.DropTable(
                name: "imaging_studies");

            migrationBuilder.DropTable(
                name: "patients");
        }
    }
}
