using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidationSubjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "spec_sha256",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_invalidation_reason",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subject_id",
                table: "task_validations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "validation_subjects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<int>(type: "INTEGER", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository_path = table.Column<string>(type: "TEXT", nullable: false),
                    implementation_sha = table.Column<string>(type: "TEXT", nullable: false),
                    spec_path = table.Column<string>(type: "TEXT", nullable: false),
                    spec_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    required_validators_json = table.Column<string>(type: "TEXT", nullable: false),
                    required_checks_json = table.Column<string>(type: "TEXT", nullable: false),
                    invalidating_environment_json = table.Column<string>(type: "TEXT", nullable: false),
                    descriptive_metadata_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_subjects", x => x.id);
                });

            // SQLite can add this nullable reference directly. Combining the generated AddColumn and
            // AddForeignKey into one statement avoids rebuilding tasks and disabling foreign keys.
            migrationBuilder.Sql("""
                ALTER TABLE "tasks" ADD COLUMN "current_subject_id" TEXT NULL
                CONSTRAINT "FK_tasks_validation_subjects_current_subject_id"
                REFERENCES "validation_subjects" ("id") ON DELETE RESTRICT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_current_subject_id",
                table: "tasks",
                column: "current_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_subjects_task_id",
                table: "validation_subjects",
                column: "task_id");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tasks_validation_subjects_current_subject_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "validation_subjects");

            migrationBuilder.DropIndex(
                name: "IX_tasks_current_subject_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "current_subject_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "spec_sha256",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "validation_invalidation_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "subject_id",
                table: "task_validations");
        }
    }
}
