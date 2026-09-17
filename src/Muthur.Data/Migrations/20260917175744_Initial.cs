using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    registered_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_heartbeat = table.Column<long>(type: "INTEGER", nullable: false),
                    limited_until = table.Column<long>(type: "INTEGER", nullable: true),
                    summary = table.Column<string>(type: "TEXT", nullable: true),
                    harness = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    tier = table.Column<string>(type: "TEXT", nullable: true),
                    account = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: false),
                    actor_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    actor_model = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: true),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "meta",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    repo_path = table.Column<string>(type: "TEXT", nullable: false),
                    default_branch = table.Column<string>(type: "TEXT", nullable: false),
                    land_mode = table.Column<string>(type: "TEXT", nullable: false),
                    required_validators = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    owner_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    claim_expires = table.Column<long>(type: "INTEGER", nullable: true),
                    spec_path = table.Column<string>(type: "TEXT", nullable: true),
                    branch = table.Column<string>(type: "TEXT", nullable: true),
                    pr_url = table.Column<string>(type: "TEXT", nullable: true),
                    parent_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    done_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_tasks_agents_owner_agent_id",
                        column: x => x.owner_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tasks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_name",
                table: "agents",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_token_hash",
                table: "agents",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_task_id",
                table: "events",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_key",
                table: "projects",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_owner_agent_id",
                table: "tasks",
                column: "owner_agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_project_id",
                table: "tasks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_state",
                table: "tasks",
                column: "state");

            // The ledger is append-only. Enforced in the database so no code path can rewrite history.
            if (migrationBuilder.ActiveProvider.Contains("Sqlite"))
            {
                migrationBuilder.Sql("CREATE TRIGGER events_no_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT, 'events is append-only'); END;");
                migrationBuilder.Sql("CREATE TRIGGER events_no_delete BEFORE DELETE ON events BEGIN SELECT RAISE(ABORT, 'events is append-only'); END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "meta");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
