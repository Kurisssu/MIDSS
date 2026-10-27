using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFLMetricsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "aggregated_macro_f1",
                table: "fl_rounds",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "payload_bytes",
                table: "fl_model_updates",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "class_histogram_json",
                table: "fl_participants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aggregated_macro_f1",
                table: "fl_rounds");

            migrationBuilder.DropColumn(
                name: "payload_bytes",
                table: "fl_model_updates");

            migrationBuilder.DropColumn(
                name: "class_histogram_json",
                table: "fl_participants");
        }
    }
}
