using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResultMetadataToAIModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassNames",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassRecommendations",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassSeverities",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NumOutputClasses",
                table: "ai_models",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassNames",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "ClassRecommendations",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "ClassSeverities",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "NumOutputClasses",
                table: "ai_models");
        }
    }
}
