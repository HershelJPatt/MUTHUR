using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muthur.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidationClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "claim_expires",
                table: "task_validations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "claimed_by_agent_id",
                table: "task_validations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "waiting_since",
                table: "task_validations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // The scaffolded default is the CLR zero, which as a unix-millisecond timestamp reads as 1970 — every
            // row already in the table would claim to have been waiting for half a century. Rows that were decided
            // know when they were decided, so take that; the rest started waiting, as far as anything here can
            // honestly say, at the upgrade. A 'validating' task from before this change reporting its wait as
            // "since the upgrade" is honest, and there are at most a handful of them.
            migrationBuilder.Sql(
                "UPDATE task_validations SET waiting_since = COALESCE(at, CAST(strftime('%s', 'now') AS INTEGER) * 1000);");

            migrationBuilder.CreateIndex(
                name: "IX_task_validations_claimed_by_agent_id",
                table: "task_validations",
                column: "claimed_by_agent_id");

            migrationBuilder.AddForeignKey(
                name: "FK_task_validations_agents_claimed_by_agent_id",
                table: "task_validations",
                column: "claimed_by_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_task_validations_agents_claimed_by_agent_id",
                table: "task_validations");

            migrationBuilder.DropIndex(
                name: "IX_task_validations_claimed_by_agent_id",
                table: "task_validations");

            migrationBuilder.DropColumn(
                name: "claim_expires",
                table: "task_validations");

            migrationBuilder.DropColumn(
                name: "claimed_by_agent_id",
                table: "task_validations");

            migrationBuilder.DropColumn(
                name: "waiting_since",
                table: "task_validations");
        }
    }
}
