using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class ExtractRepositoryFromPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create Repositories table
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RepoUrl = table.Column<string>(type: "text", nullable: false),
                    EncryptedGitToken = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Name",
                table: "Repositories",
                column: "Name",
                unique: true);

            // 2. Add nullable RepositoryId column to Pipelines
            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryId",
                table: "Pipelines",
                type: "uuid",
                nullable: true);

            // 3. Migrate data: deduplicate by (RepoUrl, EncryptedGitToken),
            //    name each repository after the first pipeline in the group.
            //    Handles NULL EncryptedGitToken with IS NOT DISTINCT FROM.
            migrationBuilder.Sql("""
                WITH grouped AS (
                    SELECT
                        "RepoUrl",
                        "EncryptedGitToken",
                        MIN("Name") AS repo_name,
                        MIN("CreatedAt") AS created_at
                    FROM "Pipelines"
                    GROUP BY "RepoUrl", "EncryptedGitToken"
                )
                INSERT INTO "Repositories" ("Id", "Name", "RepoUrl", "EncryptedGitToken", "CreatedAt")
                SELECT
                    gen_random_uuid(),
                    repo_name,
                    "RepoUrl",
                    "EncryptedGitToken",
                    created_at
                FROM grouped;
                """);

            // 4. Set RepositoryId on all pipelines
            migrationBuilder.Sql("""
                UPDATE "Pipelines" p
                SET "RepositoryId" = r."Id"
                FROM "Repositories" r
                WHERE p."RepoUrl" = r."RepoUrl"
                  AND p."EncryptedGitToken" IS NOT DISTINCT FROM r."EncryptedGitToken";
                """);

            // 5. Make RepositoryId non-nullable
            migrationBuilder.AlterColumn<Guid>(
                name: "RepositoryId",
                table: "Pipelines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // 6. Drop old columns
            migrationBuilder.DropColumn(
                name: "EncryptedGitToken",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "RepoUrl",
                table: "Pipelines");

            // 7. Add FK and index
            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepositoryId",
                table: "Pipelines",
                column: "RepositoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_Repositories_RepositoryId",
                table: "Pipelines",
                column: "RepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_Repositories_RepositoryId",
                table: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_RepositoryId",
                table: "Pipelines");

            // Restore old columns
            migrationBuilder.AddColumn<string>(
                name: "RepoUrl",
                table: "Pipelines",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedGitToken",
                table: "Pipelines",
                type: "text",
                nullable: true);

            // Copy data back from Repositories
            migrationBuilder.Sql("""
                UPDATE "Pipelines" p
                SET "RepoUrl" = r."RepoUrl",
                    "EncryptedGitToken" = r."EncryptedGitToken"
                FROM "Repositories" r
                WHERE p."RepositoryId" = r."Id";
                """);

            migrationBuilder.DropColumn(
                name: "RepositoryId",
                table: "Pipelines");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
