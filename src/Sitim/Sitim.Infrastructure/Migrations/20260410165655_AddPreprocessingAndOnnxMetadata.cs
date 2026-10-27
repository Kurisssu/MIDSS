using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreprocessingAndOnnxMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OnnxInputSpec",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnnxOutputSpec",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreprocessingImageSize",
                table: "ai_models",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreprocessingMean",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreprocessingMethod",
                table: "ai_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreprocessingStd",
                table: "ai_models",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnnxInputSpec",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "OnnxOutputSpec",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "PreprocessingImageSize",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "PreprocessingMean",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "PreprocessingMethod",
                table: "ai_models");

            migrationBuilder.DropColumn(
                name: "PreprocessingStd",
                table: "ai_models");
        }
    }
}
