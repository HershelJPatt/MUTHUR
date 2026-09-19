using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectPushState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_push_at",
                table: "projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_push_attempt_at",
                table: "projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_push_error",
                table: "projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_pushed_commit",
                table: "projects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_push_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "last_push_attempt_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "last_push_error",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "last_pushed_commit",
                table: "projects");
        }
    }
}
