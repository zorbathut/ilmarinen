using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ilmarinen.Database.Migrations
{
    /// <inheritdoc />
    public partial class RecordJobDiskUse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DiskUsedBytes",
                table: "Jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StoppedForDisk",
                table: "Jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorkspaceFresh",
                table: "Jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceKind",
                table: "Jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceName",
                table: "Jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiskUsedBytes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "StoppedForDisk",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "WorkspaceFresh",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "WorkspaceKind",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "WorkspaceName",
                table: "Jobs");
        }
    }
}
