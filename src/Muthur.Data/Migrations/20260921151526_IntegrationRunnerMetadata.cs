using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationRunnerMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "artifacts_directory",
                table: "integration_candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owned_worktree_path",
                table: "integration_candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "runner_process_id",
                table: "integration_candidates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "runner_started_at",
                table: "integration_candidates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "integration_runner",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "artifacts_directory",
                table: "integration_candidates");

            migrationBuilder.DropColumn(
                name: "owned_worktree_path",
                table: "integration_candidates");

            migrationBuilder.DropColumn(
                name: "runner_process_id",
                table: "integration_candidates");

            migrationBuilder.DropColumn(
                name: "runner_started_at",
                table: "integration_candidates");

            migrationBuilder.DropColumn(
                name: "integration_runner",
                table: "agents");
        }
    }
}
