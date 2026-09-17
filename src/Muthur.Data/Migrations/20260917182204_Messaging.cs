using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "founder_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    task_id = table.Column<int>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_name = table.Column<string>(type: "TEXT", nullable: false),
                    question = table.Column<string>(type: "TEXT", nullable: false),
                    options = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    answer = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    answered_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_founder_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    from_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    from_name = table.Column<string>(type: "TEXT", nullable: false),
                    to_kind = table.Column<string>(type: "TEXT", nullable: false),
                    to_key = table.Column<string>(type: "TEXT", nullable: true),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    blocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    read_at = table.Column<long>(type: "INTEGER", nullable: true),
                    read_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_founder_requests_status",
                table: "founder_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_messages_to_kind_to_key_read_at",
                table: "messages",
                columns: new[] { "to_kind", "to_key", "read_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "founder_requests");

            migrationBuilder.DropTable(
                name: "messages");
        }
    }
}
