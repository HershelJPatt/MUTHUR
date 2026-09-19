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
            // Three columns and a foreign key. SQLite adds columns in place but cannot add a foreign key that
            // way, so this is one rebuild rather than three ALTERs and a scaffolded one: EF's own rebuild wraps
            // the swap in PRAGMA foreign_keys = 0, which cannot run inside a transaction, so the migration is
            // applied without one and an interrupted upgrade leaves a founder's hub half-migrated. Written out,
            // it is ordinary DDL that EF runs inside the migration's transaction.
            //
            // Nothing in the schema points a foreign key at task_validations, so dropping it breaks no
            // reference, and the pragma EF would have emitted is not needed for anything here.
            migrationBuilder.Sql("""
                CREATE TABLE "task_validations_rebuilt" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_task_validations" PRIMARY KEY AUTOINCREMENT,
                    "task_id" INTEGER NOT NULL,
                    "validator_key" TEXT NOT NULL,
                    "verdict" TEXT NOT NULL,
                    "evidence" TEXT NULL,
                    "agent_id" TEXT NULL,
                    "at" INTEGER NULL,
                    "claim_expires" INTEGER NULL,
                    "claimed_by_agent_id" TEXT NULL,
                    "waiting_since" INTEGER NOT NULL,
                    CONSTRAINT "FK_task_validations_agents_agent_id" FOREIGN KEY ("agent_id") REFERENCES "agents" ("id") ON DELETE SET NULL,
                    CONSTRAINT "FK_task_validations_agents_claimed_by_agent_id" FOREIGN KEY ("claimed_by_agent_id") REFERENCES "agents" ("id") ON DELETE SET NULL,
                    CONSTRAINT "FK_task_validations_tasks_task_id" FOREIGN KEY ("task_id") REFERENCES "tasks" ("id") ON DELETE CASCADE
                );
                """);

            // waiting_since is filled in the copy rather than defaulted and then corrected. The scaffolded
            // default is the CLR zero, which as a unix-millisecond timestamp reads as 1970 — every row already
            // in the table would claim to have been waiting for half a century. Rows that were decided know
            // when they were decided, so take that; the rest started waiting, as far as anything here can
            // honestly say, at the upgrade. A 'validating' task from before this change reporting its wait as
            // "since the upgrade" is honest, and there are at most a handful of them.
            migrationBuilder.Sql("""
                INSERT INTO "task_validations_rebuilt"
                    ("id", "task_id", "validator_key", "verdict", "evidence", "agent_id", "at",
                     "claim_expires", "claimed_by_agent_id", "waiting_since")
                SELECT "id", "task_id", "validator_key", "verdict", "evidence", "agent_id", "at",
                       NULL, NULL, COALESCE("at", CAST(strftime('%s', 'now') AS INTEGER) * 1000)
                FROM "task_validations";
                """);
            migrationBuilder.Sql("""DROP TABLE "task_validations";""");
            migrationBuilder.Sql("""ALTER TABLE "task_validations_rebuilt" RENAME TO "task_validations";""");
            migrationBuilder.Sql("""CREATE INDEX "IX_task_validations_agent_id" ON "task_validations" ("agent_id");""");
            migrationBuilder.Sql("""CREATE INDEX "IX_task_validations_claimed_by_agent_id" ON "task_validations" ("claimed_by_agent_id");""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_task_validations_task_id_validator_key" ON "task_validations" ("task_id", "validator_key");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The same rebuild in reverse, and for the same reason: dropping the foreign key is what SQLite
            // cannot do in place, so it is one written-out swap inside the migration's transaction.
            migrationBuilder.Sql("""
                CREATE TABLE "task_validations_rebuilt" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_task_validations" PRIMARY KEY AUTOINCREMENT,
                    "task_id" INTEGER NOT NULL,
                    "validator_key" TEXT NOT NULL,
                    "verdict" TEXT NOT NULL,
                    "evidence" TEXT NULL,
                    "agent_id" TEXT NULL,
                    "at" INTEGER NULL,
                    CONSTRAINT "FK_task_validations_agents_agent_id" FOREIGN KEY ("agent_id") REFERENCES "agents" ("id") ON DELETE SET NULL,
                    CONSTRAINT "FK_task_validations_tasks_task_id" FOREIGN KEY ("task_id") REFERENCES "tasks" ("id") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "task_validations_rebuilt"
                    ("id", "task_id", "validator_key", "verdict", "evidence", "agent_id", "at")
                SELECT "id", "task_id", "validator_key", "verdict", "evidence", "agent_id", "at"
                FROM "task_validations";
                """);
            migrationBuilder.Sql("""DROP TABLE "task_validations";""");
            migrationBuilder.Sql("""ALTER TABLE "task_validations_rebuilt" RENAME TO "task_validations";""");
            migrationBuilder.Sql("""CREATE INDEX "IX_task_validations_agent_id" ON "task_validations" ("agent_id");""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_task_validations_task_id_validator_key" ON "task_validations" ("task_id", "validator_key");""");
        }
    }
}
