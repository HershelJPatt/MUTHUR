using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class Outbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    channel = table.Column<string>(type: "TEXT", nullable: false),
                    address = table.Column<string>(type: "TEXT", nullable: false),
                    requires_founder_approval = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: true),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    body_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    author_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    author_name = table.Column<string>(type: "TEXT", nullable: false),
                    author_model = table.Column<string>(type: "TEXT", nullable: true),
                    reviewer_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    reviewer_name = table.Column<string>(type: "TEXT", nullable: true),
                    reviewer_model = table.Column<string>(type: "TEXT", nullable: true),
                    review_note = table.Column<string>(type: "TEXT", nullable: true),
                    reviewed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    founder_approved_at = table.Column<long>(type: "INTEGER", nullable: true),
                    sent_at = table.Column<long>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound", x => x.id);
                    table.ForeignKey(
                        name: "FK_outbound_outbound_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "outbound_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_status",
                table: "outbound",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_target_id",
                table: "outbound",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_targets_key",
                table: "outbound_targets",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound");

            migrationBuilder.DropTable(
                name: "outbound_targets");
        }
    }
}
