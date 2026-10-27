using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use IF NOT EXISTS so this migration is safe to apply even if columns were added manually.
            migrationBuilder.Sql(@"ALTER TABLE ""AspNetUsers"" ADD COLUMN IF NOT EXISTS ""FullName"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""AspNetUsers"" ADD COLUMN IF NOT EXISTS ""IsActive"" boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql(@"ALTER TABLE ""AspNetUsers"" ADD COLUMN IF NOT EXISTS ""CreatedAtUtc"" timestamp with time zone NOT NULL DEFAULT now();");

            // Activate all existing users that were created before this migration
            migrationBuilder.Sql(@"UPDATE ""AspNetUsers"" SET ""IsActive"" = true WHERE ""IsActive"" = false AND ""EmailConfirmed"" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "AspNetUsers");
        }
    }
}
