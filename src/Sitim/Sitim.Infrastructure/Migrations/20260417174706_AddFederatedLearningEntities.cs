using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sitim.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedLearningEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fl_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    total_rounds = table.Column<int>(type: "integer", nullable: false),
                    current_round = table.Column<int>(type: "integer", nullable: false),
                    external_session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    output_model_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fl_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fl_model_updates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    round_number = table.Column<int>(type: "integer", nullable: false),
                    training_loss = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                    validation_accuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    update_artifact_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fl_model_updates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fl_model_updates_fl_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "fl_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fl_model_updates_institutions_InstitutionId",
                        column: x => x.InstitutionId,
                        principalTable: "institutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fl_participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_heartbeat_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fl_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fl_participants_fl_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "fl_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fl_participants_institutions_InstitutionId",
                        column: x => x.InstitutionId,
                        principalTable: "institutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fl_rounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    round_number = table.Column<int>(type: "integer", nullable: false),
                    aggregated_loss = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                    aggregated_accuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fl_rounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fl_rounds_fl_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "fl_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fl_model_updates_InstitutionId",
                table: "fl_model_updates",
                column: "InstitutionId");

            migrationBuilder.CreateIndex(
                name: "IX_fl_model_updates_SessionId_InstitutionId_round_number",
                table: "fl_model_updates",
                columns: new[] { "SessionId", "InstitutionId", "round_number" });

            migrationBuilder.CreateIndex(
                name: "IX_fl_participants_InstitutionId",
                table: "fl_participants",
                column: "InstitutionId");

            migrationBuilder.CreateIndex(
                name: "IX_fl_participants_SessionId_InstitutionId",
                table: "fl_participants",
                columns: new[] { "SessionId", "InstitutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fl_rounds_SessionId_round_number",
                table: "fl_rounds",
                columns: new[] { "SessionId", "round_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fl_sessions_created_at_utc",
                table: "fl_sessions",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_fl_sessions_status",
                table: "fl_sessions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fl_model_updates");

            migrationBuilder.DropTable(
                name: "fl_participants");

            migrationBuilder.DropTable(
                name: "fl_rounds");

            migrationBuilder.DropTable(
                name: "fl_sessions");
        }
    }
}
