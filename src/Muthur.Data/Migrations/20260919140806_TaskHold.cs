using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hold_by",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "hold_expires",
                table: "tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hold_reason",
                table: "tasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hold_by",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "hold_expires",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "hold_reason",
                table: "tasks");
        }
    }
}
