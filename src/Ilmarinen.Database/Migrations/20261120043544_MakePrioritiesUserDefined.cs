using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class MakePrioritiesUserDefined : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False");

            migrationBuilder.CreateTable(
                name: "Priorities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false, collation: "case_insensitive"),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Priorities", x => x.Id);
                });

            // Nullable while they are filled from the old columns; the required ones are tightened below.
            migrationBuilder.AddColumn<Guid>(name: "PriorityId", table: "Workers", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "MinWorkerPriorityId", table: "Pipelines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "PriorityId", table: "Pipelines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "MinWorkerPriorityId", table: "Jobs", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "PriorityId", table: "Jobs", type: "uuid", nullable: true);

            // The old fixed values were worker Low 0, Medium 1, High 2 and job Low -1, Medium 0, High 1, and a worker minimum of Low meant any worker. Each kind gets an entry for each value something uses, ranked by the old value, so the old order holds; a kind nothing uses gets just "Default". Kind 0 is PriorityKind.Worker, 1 is PriorityKind.Job.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Workers" WHERE "Priority" NOT IN (0, 1, 2))
                        OR EXISTS (SELECT 1 FROM "Jobs" WHERE "MinWorkerPriority" NOT IN (0, 1, 2) OR "Priority" NOT IN (-1, 0, 1))
                        OR EXISTS (SELECT 1 FROM "Pipelines" WHERE "MinWorkerPriority" NOT IN (0, 1, 2) OR "Priority" NOT IN (-1, 0, 1))
                    THEN
                        RAISE EXCEPTION 'MakePrioritiesUserDefined: a worker, job or pipeline has a priority that is not one of the old Low, Medium or High values, so it has nothing to convert to. Correct those rows, then start the server again.';
                    END IF;
                END
                $$;

                INSERT INTO "Priorities" ("Id", "Kind", "Name", "Rank", "IsDefault")
                SELECT gen_random_uuid(), 0, v.name, v.value, false
                FROM (VALUES (0, 'Low'), (1, 'Medium'), (2, 'High')) AS v(value, name)
                WHERE v.value IN (
                    SELECT "Priority" FROM "Workers"
                    UNION SELECT "MinWorkerPriority" FROM "Jobs" WHERE "MinWorkerPriority" <> 0
                    UNION SELECT "MinWorkerPriority" FROM "Pipelines" WHERE "MinWorkerPriority" <> 0);

                INSERT INTO "Priorities" ("Id", "Kind", "Name", "Rank", "IsDefault")
                SELECT gen_random_uuid(), 1, v.name, v.value, false
                FROM (VALUES (-1, 'Low'), (0, 'Medium'), (1, 'High')) AS v(value, name)
                WHERE v.value IN (
                    SELECT "Priority" FROM "Jobs"
                    UNION SELECT "Priority" FROM "Pipelines");

                INSERT INTO "Priorities" ("Id", "Kind", "Name", "Rank", "IsDefault")
                SELECT gen_random_uuid(), k.kind, 'Default', 0, true
                FROM (VALUES (0), (1)) AS k(kind)
                WHERE NOT EXISTS (SELECT 1 FROM "Priorities" p WHERE p."Kind" = k.kind);

                -- Medium (worker rank 1, job rank 0) was what new workers and jobs got; where nothing used it, the lowest entry is the default.
                UPDATE "Priorities" SET "IsDefault" = true
                WHERE "Id" IN (
                    SELECT DISTINCT ON ("Kind") "Id"
                    FROM "Priorities"
                    ORDER BY "Kind", ("Rank" = CASE "Kind" WHEN 0 THEN 1 ELSE 0 END) DESC, "Rank")
                AND NOT EXISTS (SELECT 1 FROM "Priorities" d WHERE d."Kind" = "Priorities"."Kind" AND d."IsDefault");

                -- Ranks are the old values, so each old value finds its entry by rank.
                UPDATE "Workers" w SET
                    "PriorityId" = (SELECT p."Id" FROM "Priorities" p WHERE p."Kind" = 0 AND p."Rank" = w."Priority");

                UPDATE "Pipelines" x SET
                    "PriorityId" = (SELECT p."Id" FROM "Priorities" p WHERE p."Kind" = 1 AND p."Rank" = x."Priority"),
                    "MinWorkerPriorityId" = (SELECT p."Id" FROM "Priorities" p WHERE p."Kind" = 0 AND p."Rank" = x."MinWorkerPriority" AND x."MinWorkerPriority" <> 0);

                UPDATE "Jobs" x SET
                    "PriorityId" = (SELECT p."Id" FROM "Priorities" p WHERE p."Kind" = 1 AND p."Rank" = x."Priority"),
                    "MinWorkerPriorityId" = (SELECT p."Id" FROM "Priorities" p WHERE p."Kind" = 0 AND p."Rank" = x."MinWorkerPriority" AND x."MinWorkerPriority" <> 0);
                """);

            migrationBuilder.AlterColumn<Guid>(name: "PriorityId", table: "Workers", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "PriorityId", table: "Pipelines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "PriorityId", table: "Jobs", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.DropColumn(name: "Priority", table: "Workers");
            migrationBuilder.DropColumn(name: "MinWorkerPriority", table: "Pipelines");
            migrationBuilder.DropColumn(name: "Priority", table: "Pipelines");
            migrationBuilder.DropColumn(name: "MinWorkerPriority", table: "Jobs");
            migrationBuilder.DropColumn(name: "Priority", table: "Jobs");

            migrationBuilder.CreateIndex(name: "IX_Workers_PriorityId", table: "Workers", column: "PriorityId");
            migrationBuilder.CreateIndex(name: "IX_Pipelines_MinWorkerPriorityId", table: "Pipelines", column: "MinWorkerPriorityId");
            migrationBuilder.CreateIndex(name: "IX_Pipelines_PriorityId", table: "Pipelines", column: "PriorityId");
            migrationBuilder.CreateIndex(name: "IX_Jobs_MinWorkerPriorityId", table: "Jobs", column: "MinWorkerPriorityId");
            migrationBuilder.CreateIndex(name: "IX_Jobs_PriorityId", table: "Jobs", column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_Priorities_Kind_Default",
                table: "Priorities",
                column: "Kind",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_Priorities_Kind_Name",
                table: "Priorities",
                columns: new[] { "Kind", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(name: "FK_Jobs_Priorities_MinWorkerPriorityId", table: "Jobs", column: "MinWorkerPriorityId", principalTable: "Priorities", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Jobs_Priorities_PriorityId", table: "Jobs", column: "PriorityId", principalTable: "Priorities", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Pipelines_Priorities_MinWorkerPriorityId", table: "Pipelines", column: "MinWorkerPriorityId", principalTable: "Priorities", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Pipelines_Priorities_PriorityId", table: "Pipelines", column: "PriorityId", principalTable: "Priorities", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Workers_Priorities_PriorityId", table: "Workers", column: "PriorityId", principalTable: "Priorities", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Priorities are user-defined from this migration on and can't be mapped back onto the fixed Low, Medium and High values.");
        }
    }
}
