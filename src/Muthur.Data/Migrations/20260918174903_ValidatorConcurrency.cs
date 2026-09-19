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
            // Hand-corrected from the scaffolded 0: every role defined before this change keeps
            // exactly today's behaviour, which is a capacity of one.
            migrationBuilder.AddColumn<int>(
                name: "holders",
                table: "roles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // role_holds gains agent_id in its primary key, which SQLite cannot do in place. The rebuild is
            // written out rather than scaffolded, because EF's own rebuild wraps the swap in
            // PRAGMA foreign_keys = 0 — which cannot run inside a transaction, so the whole migration is applied
            // without one, and an interrupted upgrade leaves a founder's hub half-migrated and needing hands.
            // Spelled out this way it is ordinary DDL, EF runs it in the migration's transaction, and SQLite
            // makes the swap atomic.
            //
            // The pragma is not needed here at all: nothing in the schema has a foreign key pointing at
            // role_holds, so dropping it breaks no reference. Its own two foreign keys are declared on the new
            // table and are satisfied by the rows copied into it.
            migrationBuilder.Sql("""
                CREATE TABLE "role_holds_rebuilt" (
                    "role_key" TEXT NOT NULL,
                    "agent_id" TEXT NOT NULL,
                    "acquired_at" INTEGER NOT NULL,
                    "lease_expires" INTEGER NOT NULL,
                    CONSTRAINT "PK_role_holds" PRIMARY KEY ("role_key", "agent_id"),
                    CONSTRAINT "FK_role_holds_agents_agent_id" FOREIGN KEY ("agent_id") REFERENCES "agents" ("id") ON DELETE CASCADE,
                    CONSTRAINT "FK_role_holds_roles_role_key" FOREIGN KEY ("role_key") REFERENCES "roles" ("key") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "role_holds_rebuilt" ("role_key", "agent_id", "acquired_at", "lease_expires")
                SELECT "role_key", "agent_id", "acquired_at", "lease_expires" FROM "role_holds";
                """);
            migrationBuilder.Sql("""DROP TABLE "role_holds";""");
            migrationBuilder.Sql("""ALTER TABLE "role_holds_rebuilt" RENAME TO "role_holds";""");
            // The old table's index went with it, and the name is free again only after the rename.
            migrationBuilder.Sql("""CREATE INDEX "IX_role_holds_agent_id" ON "role_holds" ("agent_id");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one holder per role: the same rebuild in reverse. A role several agents hold cannot fit a
            // primary key of role_key alone, so the earliest hold is the one that survives — which is exactly
            // what a capacity of one meant before this migration existed.
            migrationBuilder.Sql("""
                CREATE TABLE "role_holds_rebuilt" (
                    "role_key" TEXT NOT NULL,
                    "agent_id" TEXT NOT NULL,
                    "acquired_at" INTEGER NOT NULL,
                    "lease_expires" INTEGER NOT NULL,
                    CONSTRAINT "PK_role_holds" PRIMARY KEY ("role_key"),
                    CONSTRAINT "FK_role_holds_agents_agent_id" FOREIGN KEY ("agent_id") REFERENCES "agents" ("id") ON DELETE CASCADE,
                    CONSTRAINT "FK_role_holds_roles_role_key" FOREIGN KEY ("role_key") REFERENCES "roles" ("key") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "role_holds_rebuilt" ("role_key", "agent_id", "acquired_at", "lease_expires")
                SELECT "role_key", "agent_id", MIN("acquired_at"), "lease_expires"
                FROM "role_holds" GROUP BY "role_key";
                """);
            migrationBuilder.Sql("""DROP TABLE "role_holds";""");
            migrationBuilder.Sql("""ALTER TABLE "role_holds_rebuilt" RENAME TO "role_holds";""");
            migrationBuilder.Sql("""CREATE INDEX "IX_role_holds_agent_id" ON "role_holds" ("agent_id");""");

            migrationBuilder.DropColumn(
                name: "holders",
                table: "roles");
        }
    }
}
