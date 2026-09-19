using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentConductorStaffed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "conductor_staffed",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conductor_staffed",
                table: "agents");
        }
    }
}
