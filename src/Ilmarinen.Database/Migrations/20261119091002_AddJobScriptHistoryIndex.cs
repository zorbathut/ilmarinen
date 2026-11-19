using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddJobScriptHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RepoUrl_ScriptPath_Status_CompletedAt",
                table: "Jobs",
                columns: new[] { "RepoUrl", "ScriptPath", "Status", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_RepoUrl_ScriptPath_Status_CompletedAt",
                table: "Jobs");
        }
    }
}
