using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetModalityToAIModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetectablePathologies",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportedRegions",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetModality",
                table: "ai_models",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectablePathologies",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "SupportedRegions",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "TargetModality",
                table: "ai_models");
        }
    }
}
