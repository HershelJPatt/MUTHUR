using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    signature = table.Column<string>(type: "TEXT", nullable: false),
                    execution_path = table.Column<string>(type: "TEXT", nullable: false),
                    configuration = table.Column<string>(type: "TEXT", nullable: false),
                    condition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    diagnosis = table.Column<string>(type: "TEXT", nullable: false),
                    workaround = table.Column<string>(type: "TEXT", nullable: false),
                    authorization_reference = table.Column<string>(type: "TEXT", nullable: false),
                    recovery_condition = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.id);
                    table.ForeignKey(
                        name: "FK_incidents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_observations",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    incident_id = table.Column<int>(type: "INTEGER", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: false),
                    run_id = table.Column<string>(type: "TEXT", nullable: true),
                    evidence = table.Column<string>(type: "TEXT", nullable: false),
                    signature = table.Column<string>(type: "TEXT", nullable: false),
                    execution_path = table.Column<string>(type: "TEXT", nullable: false),
                    configuration = table.Column<string>(type: "TEXT", nullable: false),
                    condition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    unlink_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_observations", x => x.id);
                    table.ForeignKey(
                        name: "FK_incident_observations_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_observations_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_suppressions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    incident_id = table.Column<int>(type: "INTEGER", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: false),
                    assignment = table.Column<string>(type: "TEXT", nullable: false),
                    condition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    evidence_observation_id = table.Column<int>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    released_at = table.Column<long>(type: "INTEGER", nullable: true),
                    release_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_suppressions", x => x.id);
                    table.ForeignKey(
                        name: "FK_incident_suppressions_incident_observations_evidence_observation_id",
                        column: x => x.evidence_observation_id,
                        principalTable: "incident_observations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_suppressions_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_suppressions_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incident_observations_incident_id",
                table: "incident_observations",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_observations_task_id",
                table: "incident_observations",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_suppressions_evidence_observation_id",
                table: "incident_suppressions",
                column: "evidence_observation_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_suppressions_incident_id_task_id_assignment_condition_version",
                table: "incident_suppressions",
                columns: new[] { "incident_id", "task_id", "assignment", "condition_version" },
                unique: true,
                filter: "active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_incident_suppressions_task_id",
                table: "incident_suppressions",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_project_id",
                table: "incidents",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_suppressions");

            migrationBuilder.DropTable(
                name: "incident_observations");

            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}
