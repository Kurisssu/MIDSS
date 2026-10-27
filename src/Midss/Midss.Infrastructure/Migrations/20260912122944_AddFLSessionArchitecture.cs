using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Midss.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFLSessionArchitecture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "architecture",
                table: "fl_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "architecture",
                table: "fl_sessions");
        }
    }
}
