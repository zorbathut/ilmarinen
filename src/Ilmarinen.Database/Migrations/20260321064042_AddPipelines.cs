using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PipelineId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RepoUrl = table.Column<string>(type: "text", nullable: false),
                    DefaultRef = table.Column<string>(type: "text", nullable: false),
                    ScriptPath = table.Column<string>(type: "text", nullable: false),
                    EncryptedGitToken = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PipelineId",
                table: "Jobs",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_Name",
                table: "Pipelines",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PipelineId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PipelineId",
                table: "Jobs");
        }
    }
}
