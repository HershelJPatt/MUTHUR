using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    assignment_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: false),
                    subject_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository_path = table.Column<string>(type: "TEXT", nullable: false),
                    default_branch = table.Column<string>(type: "TEXT", nullable: false),
                    target_sha = table.Column<string>(type: "TEXT", nullable: false),
                    implementation_sha = table.Column<string>(type: "TEXT", nullable: false),
                    candidate_sha = table.Column<string>(type: "TEXT", nullable: true),
                    tree_sha = table.Column<string>(type: "TEXT", nullable: true),
                    required_checks_json = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_expires = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    already_included = table.Column<bool>(type: "INTEGER", nullable: false),
                    promotion_intent_at = table.Column<long>(type: "INTEGER", nullable: true),
                    promoted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    evidence_json = table.Column<string>(type: "TEXT", nullable: true),
                    evidence_sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    failure_code = table.Column<string>(type: "TEXT", nullable: true),
                    failure_message = table.Column<string>(type: "TEXT", nullable: true),
                    failure_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_integration_candidates_agents_assigned_agent_id",
                        column: x => x.assigned_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integration_candidates_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integration_candidates_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integration_candidates_validation_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "validation_subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Add the nullable reference directly, preserving tasks without a SQLite table rebuild.
            migrationBuilder.Sql("""
                ALTER TABLE "tasks" ADD COLUMN "current_integration_candidate_id" TEXT NULL
                CONSTRAINT "FK_tasks_integration_candidates_current_integration_candidate_id"
                REFERENCES "integration_candidates" ("id") ON DELETE RESTRICT;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_tasks_current_integration_candidate_id",
                table: "tasks",
                column: "current_integration_candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_assigned_agent_id",
                table: "integration_candidates",
                column: "assigned_agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_assignment_id",
                table: "integration_candidates",
                column: "assignment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_project_id",
                table: "integration_candidates",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_state",
                table: "integration_candidates",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_subject_id_attempt",
                table: "integration_candidates",
                columns: new[] { "subject_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_candidates_task_id_created_at",
                table: "integration_candidates",
                columns: new[] { "task_id", "created_at" });


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tasks_integration_candidates_current_integration_candidate_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "integration_candidates");

            migrationBuilder.DropIndex(
                name: "IX_tasks_current_integration_candidate_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "current_integration_candidate_id",
                table: "tasks");
        }
    }
}
