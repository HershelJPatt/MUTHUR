using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    brief_md = table.Column<string>(type: "TEXT", nullable: false),
                    is_validator = table.Column<bool>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "task_validations",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    task_id = table.Column<int>(type: "INTEGER", nullable: false),
                    validator_key = table.Column<string>(type: "TEXT", nullable: false),
                    verdict = table.Column<string>(type: "TEXT", nullable: false),
                    evidence = table.Column<string>(type: "TEXT", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_validations", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_validations_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_task_validations_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_holds",
                columns: table => new
                {
                    role_key = table.Column<string>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    acquired_at = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_expires = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_holds", x => x.role_key);
                    table.ForeignKey(
                        name: "FK_role_holds_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_holds_roles_role_key",
                        column: x => x.role_key,
                        principalTable: "roles",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_role_holds_agent_id",
                table: "role_holds",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_validations_agent_id",
                table: "task_validations",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_validations_task_id_validator_key",
                table: "task_validations",
                columns: new[] { "task_id", "validator_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_holds");

            migrationBuilder.DropTable(
                name: "task_validations");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
