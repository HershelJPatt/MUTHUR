using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_limits",
                columns: table => new
                {
                    account = table.Column<string>(type: "TEXT", nullable: false),
                    limited_until = table.Column<long>(type: "INTEGER", nullable: false),
                    reported_by = table.Column<string>(type: "TEXT", nullable: false),
                    reported_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_limits", x => x.account);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_limits");
        }
    }
}
