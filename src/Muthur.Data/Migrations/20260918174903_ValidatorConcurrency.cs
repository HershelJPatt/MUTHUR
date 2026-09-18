using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidatorConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_role_holds",
                table: "role_holds");

            // Hand-corrected from the scaffolded 0: every role defined before this change keeps
            // exactly today's behaviour, which is a capacity of one.
            migrationBuilder.AddColumn<int>(
                name: "holders",
                table: "roles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_role_holds",
                table: "role_holds",
                columns: new[] { "role_key", "agent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_role_holds",
                table: "role_holds");

            migrationBuilder.DropColumn(
                name: "holders",
                table: "roles");

            migrationBuilder.AddPrimaryKey(
                name: "PK_role_holds",
                table: "role_holds",
                column: "role_key");
        }
    }
}
