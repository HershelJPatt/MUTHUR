using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ingest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ingest_sources",
                table: "projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "inbound",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    author = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    claimed_by_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    task_id = table.Column<int>(type: "INTEGER", nullable: true),
                    resolution = table.Column<string>(type: "TEXT", nullable: true),
                    received_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbound", x => x.id);
                    table.ForeignKey(
                        name: "FK_inbound_agents_claimed_by_agent_id",
                        column: x => x.claimed_by_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_inbound_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ingest_cursors",
                columns: table => new
                {
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    cursor = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingest_cursors", x => x.source);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbound_claimed_by_agent_id",
                table: "inbound",
                column: "claimed_by_agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_inbound_project_id",
                table: "inbound",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_inbound_source_external_id",
                table: "inbound",
                columns: new[] { "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbound_status",
                table: "inbound",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbound");

            migrationBuilder.DropTable(
                name: "ingest_cursors");

            migrationBuilder.DropColumn(
                name: "ingest_sources",
                table: "projects");
        }
    }
}
