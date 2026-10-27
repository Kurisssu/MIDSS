using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakePatientIdUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_patients_PatientId",
                table: "patients");

            migrationBuilder.CreateIndex(
                name: "IX_patients_PatientId",
                table: "patients",
                column: "PatientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_patients_PatientId",
                table: "patients");

            migrationBuilder.CreateIndex(
                name: "IX_patients_PatientId",
                table: "patients",
                column: "PatientId");
        }
    }
}
